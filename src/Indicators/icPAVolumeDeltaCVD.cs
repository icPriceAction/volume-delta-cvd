// =========================================================================================
// Tool Name:    icPA - Volume Delta CVD
// Class:        icPAVolumeDeltaCVD
// Category:     Indicator
// Version:      2.4.3
// Platform:     NinjaTrader 8.0+
// Author:       Istvan Cebrian
// Brand:        icPriceAction
// Website:      https://icpriceaction.com
// Repository:   https://github.com/icPriceAction/volume-delta-cvd
// YouTube:      https://youtube.com/@icPriceAction
// Support:      https://buymeacoffee.com/icpriceaction
//
// License:      Free for personal trading use. Commercial resale, redistribution,
//               or repackaging is strictly prohibited. See LICENSE in official repository.
//
// Description:  Tick-accurate per-bar Volume Delta and Cumulative Volume Delta built from a
//               hidden 1-tick series (no Tick Replay required), with CVD resets, signal line,
//               delta flips, price/CVD divergences and real-time alerts.
//
// Changelog:
//   v2.4.3              - Initial public release. Cleaned and optimized by icPriceAction.
//   v2.4.2              - Properties regrouped in a logical order with consistent names and
//                           concise descriptions.
//   v2.4.1              - Implementation notes moved into the file header; changelog added.
//   v2.4.0              - Audit fixes: closed-bar evaluation one bar later (no missed swings on
//                           shared closing timestamps), Calculate forced to On Each Tick, delta
//                           flips require a zero cross, unique alert IDs, per-trade delta-threshold
//                           alert, signal line in auto-scale, faster label measuring.
//   v2.3.0              - CVD divergences (price and/or indicator panel), real-time alerts
//                           (divergence, delta flip, CVD zero cross, signal cross, delta threshold),
//                           DivergenceSignal public series.
//   v2.2.0              - Min Trade Size filter, delta % of volume, delta flip markers, per-bar
//                           labels, price-bar colouring, TotalVolume/DeltaPercent/DeltaFlip series.
//   v2.1.1              - Plot line explicitly hidden in candlestick modes.
//   v2.1.0              - CVD reset modes (Session, Daily At Time, Weekly, Anchor, Never) with
//                           time zones, signal line (EMA/SMA), zero line, public data series.
//   v2.0.0              - No Tick Replay required: trade-to-bar mapping rewritten (volume
//                           matching / time mapping), tick-rule fallback, cached SharpDX brushes,
//                           candle width follows chart zoom, diagnostics output.
//   v1.9.0              - Original version: Volume Delta / CVD candles and CVD line from a
//                           1-tick series.
// =========================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX.Direct2D1;
#endregion

// Enums are global (outside any namespace) so the generated code region resolves them from the
// Indicators, MarketAnalyzerColumns and Strategies namespaces. Prefixed to avoid collisions.
public enum icPAVolumeDeltaCVDMode { VolumeDelta, CumulativeVolumeDelta }
public enum icPAVolumeDeltaCVDDrawStyle { Candlestick, Line }
public enum icPAVolumeDeltaCVDResetMode { Session, DailyAtTime, Weekly, Anchor, Never }
public enum icPAVolumeDeltaCVDTimeZone { Eastern, Central, Pacific, London, CentralEurope, Tokyo, HongKong, Sydney, UTC, Exchange, Platform }
public enum icPAVolumeDeltaCVDMaType { EMA, SMA }
public enum icPAVolumeDeltaCVDLabelMode { None, Delta, DeltaPercent, DeltaAndPercent, BuySellVolume }
public enum icPAVolumeDeltaCVDBarColorMode { Off, DeltaSign, DeltaPercentThreshold, DeltaFlips }

namespace NinjaTrader.NinjaScript.Indicators.icPA
{
    // =====================================================================================
    // IMPLEMENTATION NOTES
    //   - Trade-to-bar mapping does not rely on CurrentBars[0] timing:
    //       * Time-based primaries (Second/Minute/Day/Week/Month/Year, incl. Heiken Ashi on
    //         those): each trade goes to the primary bar whose timestamp covers the trade.
    //       * All other primaries (Tick, Volume, Range, Renko, ...): trades are matched to
    //         primary bars by volume. Each primary bar's volume is exactly the sum of its trades,
    //         so filling bars volume-by-volume reproduces the exact bar boundaries.
    //   - CVD resets are applied at a bar boundary (the first bar that starts at or after the
    //     reset moment), so CVD candles never break mid-bar.
    //   - The Min Trade Size filter only affects which trades count toward delta. All volume
    //     still counts toward bar mapping, so filtering never shifts bar boundaries.
    //   - Divergences compare consecutive price swing pivots with CVD at those pivots. A pivot is
    //     confirmed only after Swing Strength bars have closed on its right. Pivots separated by
    //     a CVD reset are never compared.
    //   - The primary series is processed from bar 0 (no BarsRequiredToPlot early return): data
    //     validity is governed by session sync, and every bar must be evaluated in order.
    //
    // REQUIREMENTS
    //   - Chart data loaded by Days (not Bars), so the 1-tick series covers the full history.
    //   - "Break at EOD" enabled for Tick, Volume and Range charts.
    //   - Historical accuracy depends on the data provider supplying historical bid/ask
    //     (enable Print Diagnostics to check).
    // =====================================================================================

    /// <summary>
    /// Volume Delta / CVD built from a hidden 1-tick series (no Tick Replay required).
    /// </summary>
    public class icPAVolumeDeltaCVD : Indicator
    {
        // Per-primary-bar record, indexed by the primary series' absolute bar index
        private class BarDelta
        {
            public bool   Visited;
            public double BuyVol;
            public double SellVol;
            public double TotalVol;     // volume of counted trades (incl. unclassified)
            public double DeltaHigh;
            public double DeltaLow;
            public double DeltaClose;
            public double CvdOpen;
            public double Signal;
            public bool   HasSignal;
            public int    Epoch;        // increments on every CVD reset
            public int    Div;          // +1 bullish / -1 bearish divergence confirmed on this bar

            // Cached label text (rebuilt only when the bar's volumes change)
            public string LabelText0, LabelText1;
            public int    LabelSign0, LabelSign1, LabelRows;
            public double LabelKeyBuy = double.NaN, LabelKeySell, LabelKeyTotal;

            public double CvdClose { get { return CvdOpen + DeltaClose; } }
            public double DeltaPercent { get { return TotalVol > 0 ? 100.0 * DeltaClose / TotalVol : 0; } }
        }

        private class SwingPoint
        {
            public int    Idx;
            public double Price;
            public double Cvd;
            public int    Epoch;
        }

        private class DivergenceLine
        {
            public int    FromIdx, ToIdx;
            public double FromPrice, ToPrice;
            public double FromCvd, ToCvd;
            public bool   Bullish;
        }

        private const double VolEpsilon = 1e-9;

        // Maximum number of divergence lines kept on the price panel (oldest are removed)
        private const int MaxPriceDivLines = 500;

        private readonly object dataLock = new object();
        private List<BarDelta> barData;

        // Reused per primary update (avoids a list allocation on every tick)
        private readonly List<Action> pendingActions = new List<Action>();

        // Trade-to-bar mapping state
        private bool     useTimeMapping;
        private bool     isDailyOrLonger;
        private bool     synced;
        private int      mapBar = -1;
        private int      searchFrom;
        private double   filledVol;
        private double   runningCvd;
        private int      cvdEpoch;
        private bool     pendingReset;
        private int      unsyncedSessionStarts;
        private bool     syncWarningPrinted;
        private SessionIterator sessionIterator;
        private DateTime currentTradingDay = DateTime.MinValue;

        // Reset scheduling
        private TimeZoneInfo platformTz;
        private TimeZoneInfo resetTz;
        private DateTime     anchorPlatformTime = DateTime.MaxValue;
        private DateTime     nextReset = DateTime.MaxValue;
        private DateTime     lastWeekKey = DateTime.MinValue;

        // Trade classification state
        private double lastTradePrice = double.NaN;
        private int    tickRuleDir;

        // Closed-bar evaluation (divergences, bar-close alerts)
        private int        lastEvaluatedBar = -1;
        private SwingPoint lastSwingHigh, lastSwingLow;
        private List<DivergenceLine> divergences;
        private Queue<string> priceDivTags;
        private int        lastDeltaAlertBar = -1;

        // Diagnostics
        private long cntQuote, cntInsideSpread, cntNoQuote, cntUnclassified, cntMapCorrections;
        private bool diagnosticsPrinted;

        // Public data series (for strategies / other indicators)
        private Series<double> buyVolume, sellVolume, totalVolume, delta, deltaHigh, deltaLow, deltaPercent, deltaFlip,
                               cvd, cvdOpen, cvdHigh, cvdLow, divergence;

        // Frozen WPF brushes used from the data thread (price-bar colouring, drawing objects)
        private System.Windows.Media.Brush priceBarUp, priceBarDown, divBullBrush, divBearBrush;

        // Cached SharpDX / DirectWrite resources (created in OnRenderTargetChanged, never per frame)
        private SharpDX.Direct2D1.Brush dxUp, dxDown, dxOutline, dxZero, dxFlipUp, dxFlipDown, dxNeutral, dxDivBull, dxDivBear;
        private SharpDX.Direct2D1.PathGeometry flipTriangle;
        private SharpDX.DirectWrite.TextFormat labelTextFormat;
        private string measuredLabel;
        private float  measuredLabelWidth;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Tick-accurate Volume Delta and Cumulative Volume Delta built from a hidden 1-tick series, with CVD resets, signal line, delta flips, price/CVD divergences and alerts. No Tick Replay required; runs On Each Tick. Load chart data by Days and enable Break at EOD on Tick, Volume and Range charts.\r\n"
                            + "Developed by Istvan Cebrian | https://icpriceaction.com\r"
					        + "Support this free tool: https://buymeacoffee.com/icpriceaction";
                Name = "icPA - Volume Delta CVD";
                Calculate = Calculate.OnEachTick;
                IsOverlay = false;
                DisplayInDataBox = true;
                DrawOnPricePanel = false;
                DrawHorizontalGridLines = true;
                DrawVerticalGridLines = true;
                PaintPriceMarkers = true;
                ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                // Must process every tick in order; suspended catch-up would break real-time mapping
                IsSuspendedWhileInactive = false;

                // 1. General
                IndicatorMode = icPAVolumeDeltaCVDMode.VolumeDelta;
                CVDStyle = icPAVolumeDeltaCVDDrawStyle.Candlestick;
                MinTradeSize = 1;
                ResetMode = icPAVolumeDeltaCVDResetMode.Session;
                ResetTime = new DateTime(2000, 1, 1, 9, 30, 0);
                ResetTimeZone = icPAVolumeDeltaCVDTimeZone.Eastern;
                AnchorTime = DateTime.Today.AddHours(9.5);
                SignalType = icPAVolumeDeltaCVDMaType.EMA;
                SignalPeriod = 20;
                PrintDiagnostics = false;

                // 2. Visuals (icPriceAction palette for a #E6E7E8 chart background)
                UpColor = MakeBrush(0x4C, 0x8C, 0x6A);          // muted green
                DownColor = MakeBrush(0xB5, 0x52, 0x4C);        // muted brick red
                OutlineColor = Brushes.Black;
                OutlineWidth = 1;

                CvdLineColor = MakeBrush(0x11, 0x12, 0x13);     // brand charcoal
                CvdLineWidth = 2;
                CvdDashStyle = DashStyleHelper.Solid;

                ShowSignalLine = false;

                ShowZeroLine = true;
                ZeroLineColor = Brushes.DimGray;
                ZeroLineWidth = 1;

                ShowDeltaFlips = false;
                FlipUpColor = MakeBrush(0x1F, 0x7A, 0x8C);      // muted teal
                FlipDownColor = MakeBrush(0xA3, 0x5C, 0x1F);    // burnt orange

                LabelMode = icPAVolumeDeltaCVDLabelMode.None;
                LabelFont = new NinjaTrader.Gui.Tools.SimpleFont("Arial", 10);

                PriceBarColorMode = icPAVolumeDeltaCVDBarColorMode.Off;
                PriceBarUpColor = MakeBrush(0x4C, 0x8C, 0x6A);
                PriceBarDownColor = MakeBrush(0xB5, 0x52, 0x4C);

                ShowDivergences = false;
                DivOnPricePanel = true;
                DivOnIndicatorPanel = true;
                BullishDivColor = MakeBrush(0x2F, 0x6F, 0xA3);  // steel blue
                BearishDivColor = MakeBrush(0x9C, 0x4A, 0x7A);  // muted plum
                DivLineWidth = 2;

                // 3. Offsets & Thresholds
                FlipMinExtreme = 100;
                PriceBarPercentThreshold = 20;
                SwingStrength = 5;
                DivMaxBars = 60;

                // 4. Alerts
                EnableAlerts = false;
                AlertOnDivergence = true;
                AlertOnFlip = false;
                AlertOnZeroCross = false;
                AlertOnSignalCross = false;
                AlertOnDeltaThreshold = false;
                AlertDeltaThreshold = 500;
                AlertSound = "Alert2.wav";

                // Plot 0: Data Box / price marker readout, and the line in CVD Line mode
                AddPlot(Brushes.Transparent, "DeltaOutput");
                // Plot 1: signal line (moving average of the active output)
                AddPlot(MakeBrush(0xC0, 0x8A, 0x2E), "Signal"); // muted amber
            }
            else if (State == State.Configure)
            {
                // Every trade on the 1-tick series must be processed. "On price change" would skip
                // trades at an unchanged price and "On bar close" would break real-time mapping.
                Calculate = Calculate.OnEachTick;

                AddDataSeries(BarsPeriodType.Tick, 1);

                if (IndicatorMode == icPAVolumeDeltaCVDMode.CumulativeVolumeDelta && CVDStyle == icPAVolumeDeltaCVDDrawStyle.Line)
                {
                    Plots[0].Brush = CvdLineColor;
                    Plots[0].Width = CvdLineWidth;
                    Plots[0].DashStyleHelper = CvdDashStyle;
                    Plots[0].PlotStyle = PlotStyle.Line;
                }
                else
                {
                    // Plot settings carry over when properties are edited or a template is applied,
                    // so explicitly hide the line in candlestick modes (keeps Data Box readout only).
                    Plots[0].Brush = Brushes.Transparent;
                }
            }
            else if (State == State.DataLoaded)
            {
                barData = new List<BarDelta>(Math.Max(16, BarsArray[0].Count));
                divergences = new List<DivergenceLine>();
                priceDivTags = new Queue<string>();

                buyVolume    = new Series<double>(this);
                sellVolume   = new Series<double>(this);
                totalVolume  = new Series<double>(this);
                delta        = new Series<double>(this);
                deltaHigh    = new Series<double>(this);
                deltaLow     = new Series<double>(this);
                deltaPercent = new Series<double>(this);
                deltaFlip    = new Series<double>(this);
                cvd          = new Series<double>(this);
                cvdOpen      = new Series<double>(this);
                cvdHigh      = new Series<double>(this);
                cvdLow       = new Series<double>(this);
                divergence   = new Series<double>(this);

                priceBarUp   = FrozenCopy(PriceBarUpColor);
                priceBarDown = FrozenCopy(PriceBarDownColor);
                divBullBrush = FrozenCopy(BullishDivColor);
                divBearBrush = FrozenCopy(BearishDivColor);

                BarsPeriodType pt = BarsArray[0].BarsPeriod.BarsPeriodType;
                if (pt == BarsPeriodType.HeikenAshi)
                    pt = BarsArray[0].BarsPeriod.BaseBarsPeriodType;

                isDailyOrLonger = pt == BarsPeriodType.Day || pt == BarsPeriodType.Week
                               || pt == BarsPeriodType.Month || pt == BarsPeriodType.Year;
                useTimeMapping  = isDailyOrLonger || pt == BarsPeriodType.Second || pt == BarsPeriodType.Minute;

                // Prerequisite check (printed once per load): volume matching aligns at session starts
                if (!useTimeMapping && !BarsArray[0].IsResetOnNewTradingDay)
                {
                    syncWarningPrinted = true;
                    Print(Name + ": 'Break at EOD' is disabled on the chart's data series. Enable it for Tick, Volume, Range and other non-time-based bars, otherwise delta may not align to the primary bars.");
                }

                sessionIterator = new SessionIterator(BarsArray[1]);

                platformTz = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo ?? TimeZoneInfo.Local;
                resetTz    = ResolveTimeZone(ResetTimeZone);
                try
                {
                    anchorPlatformTime = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(AnchorTime, DateTimeKind.Unspecified), resetTz, platformTz);
                }
                catch
                {
                    anchorPlatformTime = AnchorTime;
                }

                synced = false;
                mapBar = -1;
                searchFrom = 0;
                filledVol = 0;
                runningCvd = 0;
                cvdEpoch = 0;
                pendingReset = false;
                nextReset = DateTime.MaxValue;
                lastWeekKey = DateTime.MinValue;
                lastTradePrice = double.NaN;
                tickRuleDir = 0;
                lastEvaluatedBar = -1;
                lastSwingHigh = null;
                lastSwingLow = null;
                lastDeltaAlertBar = -1;
            }
            else if (State == State.Transition)
            {
                if (PrintDiagnostics && !diagnosticsPrinted)
                {
                    diagnosticsPrinted = true;
                    PrintDiagnosticSummary();
                }
            }
            else if (State == State.Terminated)
            {
                DisposeDxResources();
            }
        }

        protected override void OnBarUpdate()
        {
            // ==========================================
            // PRIMARY SERIES: evaluate closed bars, publish plots / public series
            // ==========================================
            if (BarsInProgress == 0)
            {
                if (barData == null) return;

                // Drawing objects and alerts are collected under the lock and executed after it
                pendingActions.Clear();

                lock (dataLock)
                {
                    // The previous bar is always complete here, historically and in real time:
                    // all of its trades (including any stamped with its closing timestamp) have
                    // been processed before this bar's first update.
                    int closedBar = CurrentBar - 1;
                    if (closedBar > lastEvaluatedBar)
                    {
                        for (int c = Math.Max(0, lastEvaluatedBar + 1); c <= closedBar; c++)
                            EvaluateClosedBar(c, pendingActions);
                        lastEvaluatedBar = closedBar;
                    }

                    // Refresh the previous bar too: its final trade(s) can be processed after
                    // this series moved on (same-timestamp ordering / real-time tick ordering).
                    if (CurrentBar > 0) PublishBar(1, CurrentBar - 1);
                    PublishBar(0, CurrentBar);
                }

                for (int i = 0; i < pendingActions.Count; i++) pendingActions[i]();
                pendingActions.Clear();
                return;
            }

            // ==========================================
            // 1-TICK SERIES: classification + mapping
            // ==========================================
            if (BarsInProgress != 1 || barData == null || BarsArray[0].Count == 0 || CurrentBars[1] < 0) return;

            var tb = BarsArray[1];
            int ti = CurrentBars[1];

            DateTime time  = tb.GetTime(ti);
            double   vol   = tb.GetVolume(ti);
            double   price = tb.GetClose(ti);
            double   bid   = tb.GetBid(ti);
            double   ask   = tb.GetAsk(ti);
            bool     sessionStart = tb.IsFirstBarOfSession;

            string thresholdMsg = null;
            int    thresholdBar = -1;

            lock (dataLock)
            {
                ProcessTrade(time, vol, price, bid, ask, sessionStart);

                // Intrabar threshold alert, checked on the trade itself so there is no one-tick lag
                if (State == State.Realtime && EnableAlerts && AlertOnDeltaThreshold
                    && synced && mapBar >= 0 && mapBar != lastDeltaAlertBar)
                {
                    double dlt = barData[mapBar].DeltaClose;
                    if (Math.Abs(dlt) >= AlertDeltaThreshold)
                    {
                        lastDeltaAlertBar = mapBar;
                        thresholdBar = mapBar;
                        thresholdMsg = "Bar delta reached " + FormatSigned(dlt);
                    }
                }
            }

            if (thresholdMsg != null)
                FireAlert("Delta", thresholdMsg, thresholdBar);
        }

        #region Trade processing

        private void ProcessTrade(DateTime t, double vol, double price, double bid, double ask, bool sessionStart)
        {
            int source;
            int dir = ClassifyTrade(price, bid, ask, out source);

            if (sessionStart) HandleSessionStart(t);
            if (!synced) return;

            // Time-scheduled resets (Daily at time / Anchor). Applied at the next bar start.
            if (t >= nextReset)
            {
                pendingReset = true;
                nextReset = ComputeNextReset(t);
            }

            if (source == 0) cntQuote++;
            else if (source == 1) cntInsideSpread++;
            else cntNoQuote++;
            if (dir == 0) cntUnclassified++;

            // Size filter is judged on the whole print, even if it gets split across bars
            bool counted = vol >= MinTradeSize - VolEpsilon;

            int lastPrimary = BarsArray[0].Count - 1;

            // A trade stamped after the current bar's timestamp cannot belong to it.
            // Primary mechanism for time-based bars; safety net for volume matching.
            while (mapBar < lastPrimary && TradeIsAfterBar(mapBar, t))
            {
                if (!useTimeMapping && BarsArray[0].GetVolume(mapBar) - filledVol > VolEpsilon)
                    cntMapCorrections++;
                StartBar(mapBar + 1);
            }

            if (useTimeMapping)
            {
                AddVolume(dir, vol, counted);
                return;
            }

            // Volume matching: fill each completed primary bar exactly to its volume,
            // splitting a trade across bars where the primary did (e.g. Volume bars).
            double remaining = vol;
            while (remaining > VolEpsilon)
            {
                // Historically all primary bars are already known. In real time only bars before
                // the current (still forming) primary bar have a final volume.
                bool closed = State != State.Realtime || mapBar < CurrentBars[0];

                if (closed && mapBar < lastPrimary)
                {
                    double capacity = BarsArray[0].GetVolume(mapBar) - filledVol;
                    if (capacity <= VolEpsilon)
                    {
                        StartBar(mapBar + 1);
                        continue;
                    }
                    double take = Math.Min(capacity, remaining);
                    AddVolume(dir, take, counted);
                    remaining -= take;
                }
                else
                {
                    AddVolume(dir, remaining, counted);
                    remaining = 0;
                }
            }
        }

        // Returns +1 buy, -1 sell, 0 unclassified.
        // source: 0 = bid/ask, 1 = inside spread (tick rule), 2 = no valid quotes (tick rule)
        private int ClassifyTrade(double price, double bid, double ask, out int source)
        {
            if (!double.IsNaN(lastTradePrice))
            {
                if (price > lastTradePrice) tickRuleDir = 1;
                else if (price < lastTradePrice) tickRuleDir = -1;
                // unchanged price keeps the previous direction (zero-tick rule)
            }
            lastTradePrice = price;

            bool quotesValid = bid > 0 && ask > 0 && ask > bid;
            if (quotesValid)
            {
                if (price >= ask) { source = 0; return 1; }
                if (price <= bid) { source = 0; return -1; }
                source = 1;
                return tickRuleDir;
            }

            source = 2;
            return tickRuleDir;
        }

        private void HandleSessionStart(DateTime t)
        {
            sessionIterator.GetNextSession(t, true);
            currentTradingDay = sessionIterator.ActualTradingDayExchange;

            if (ResetMode == icPAVolumeDeltaCVDResetMode.Weekly)
            {
                DateTime wk = WeekKey(currentTradingDay);
                if (synced && lastWeekKey != DateTime.MinValue && wk != lastWeekKey)
                    pendingReset = true;
                lastWeekKey = wk;
            }

            bool sessionReset = ResetMode == icPAVolumeDeltaCVDResetMode.Session;

            int lastPrimary = BarsArray[0].Count - 1;
            int i = Math.Max(searchFrom, Math.Max(0, mapBar));
            while (i < lastPrimary && TradeIsAfterBar(i, t)) i++;
            searchFrom = i;

            bool isFirstOfSession = BarsArray[0].IsFirstBarOfSessionByIndex(i);

            if (isFirstOfSession && (!synced || i > mapBar))
            {
                // Clean alignment point: this trade is the first trade of primary bar i
                bool firstSync = !synced;
                if (firstSync || sessionReset)
                {
                    ResetCvd();
                    pendingReset = false;
                }
                synced = true;
                StartBar(i);
                if (firstSync) nextReset = ComputeNextReset(t);
            }
            else if (!synced && useTimeMapping)
            {
                // Time mapping is exact regardless of bar alignment
                ResetCvd();
                pendingReset = false;
                synced = true;
                StartBar(i);
                nextReset = ComputeNextReset(t);
            }
            else if (synced)
            {
                // Primary bar spans the session boundary ("Break at EOD" disabled):
                // keep mapping, reset CVD at the next bar.
                if (sessionReset) pendingReset = true;
            }
            else
            {
                unsyncedSessionStarts++;
                if (unsyncedSessionStarts >= 2 && !syncWarningPrinted)
                {
                    syncWarningPrinted = true;
                    Print(Name + ": could not align to the primary bars. Enable 'Break at EOD' on the chart's data series for this bar type.");
                }
            }
        }

        private bool TradeIsAfterBar(int barIdx, DateTime t)
        {
            DateTime barTime = BarsArray[0].GetTime(barIdx);
            if (isDailyOrLonger)
                return currentTradingDay != DateTime.MinValue && currentTradingDay.Date > barTime.Date;
            return t > barTime;
        }

        private void ResetCvd()
        {
            runningCvd = 0;
            cvdEpoch++;
        }

        private void StartBar(int idx)
        {
            while (barData.Count <= idx) barData.Add(new BarDelta());

            if (pendingReset)
            {
                ResetCvd();
                pendingReset = false;
            }

            BarDelta d = barData[idx];
            d.Visited = true;
            d.BuyVol = 0;
            d.SellVol = 0;
            d.TotalVol = 0;
            d.DeltaHigh = 0;
            d.DeltaLow = 0;
            d.DeltaClose = 0;
            d.CvdOpen = runningCvd;
            d.HasSignal = false;
            d.Epoch = cvdEpoch;
            d.Div = 0;
            d.LabelKeyBuy = double.NaN;

            mapBar = idx;
            filledVol = 0;
        }

        private void AddVolume(int dir, double v, bool counted)
        {
            // All volume counts toward bar mapping, filtered or not
            filledVol += v;
            if (!counted) return;

            BarDelta d = barData[mapBar];

            if (dir > 0) d.BuyVol += v;
            else if (dir < 0) d.SellVol += v;
            d.TotalVol += v;

            double dlt = d.BuyVol - d.SellVol;
            if (dlt > d.DeltaHigh) d.DeltaHigh = dlt;
            if (dlt < d.DeltaLow) d.DeltaLow = dlt;
            d.DeltaClose = dlt;

            runningCvd = d.CvdOpen + dlt;
        }

        #endregion

        #region Reset scheduling

        // Next scheduled reset strictly after platformTime, in platform (chart) time.
        private DateTime ComputeNextReset(DateTime platformTime)
        {
            if (ResetMode == icPAVolumeDeltaCVDResetMode.Anchor)
                return anchorPlatformTime > platformTime ? anchorPlatformTime : DateTime.MaxValue;

            if (ResetMode != icPAVolumeDeltaCVDResetMode.DailyAtTime)
                return DateTime.MaxValue;

            try
            {
                DateTime local = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(platformTime, DateTimeKind.Unspecified), platformTz, resetTz);
                DateTime candidate = local.Date + ResetTime.TimeOfDay;
                if (candidate <= local) candidate = candidate.AddDays(1);

                // No resets on Saturday/Sunday: the weekend reopen continues from the last weekday reset
                while (candidate.DayOfWeek == DayOfWeek.Saturday || candidate.DayOfWeek == DayOfWeek.Sunday)
                    candidate = candidate.AddDays(1);

                return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), resetTz, platformTz);
            }
            catch
            {
                // e.g. reset time falls inside a DST gap; retry roughly a day later
                return platformTime.AddDays(1);
            }
        }

        private static DateTime WeekKey(DateTime tradingDay)
        {
            DateTime d = tradingDay.Date;
            int offset = ((int)d.DayOfWeek + 6) % 7; // Monday = 0
            return d.AddDays(-offset);
        }

        private TimeZoneInfo ResolveTimeZone(icPAVolumeDeltaCVDTimeZone tz)
        {
            string id = null;
            switch (tz)
            {
                case icPAVolumeDeltaCVDTimeZone.Platform:      return platformTz;
                case icPAVolumeDeltaCVDTimeZone.UTC:           return TimeZoneInfo.Utc;
                case icPAVolumeDeltaCVDTimeZone.Exchange:
                    try { return BarsArray[0].TradingHours.TimeZoneInfo ?? platformTz; }
                    catch { return platformTz; }
                case icPAVolumeDeltaCVDTimeZone.Eastern:       id = "Eastern Standard Time"; break;
                case icPAVolumeDeltaCVDTimeZone.Central:       id = "Central Standard Time"; break;
                case icPAVolumeDeltaCVDTimeZone.Pacific:       id = "Pacific Standard Time"; break;
                case icPAVolumeDeltaCVDTimeZone.London:        id = "GMT Standard Time"; break;
                case icPAVolumeDeltaCVDTimeZone.CentralEurope: id = "W. Europe Standard Time"; break;
                case icPAVolumeDeltaCVDTimeZone.Tokyo:         id = "Tokyo Standard Time"; break;
                case icPAVolumeDeltaCVDTimeZone.HongKong:      id = "China Standard Time"; break;
                case icPAVolumeDeltaCVDTimeZone.Sydney:        id = "AUS Eastern Standard Time"; break;
            }

            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { return platformTz; }
        }

        #endregion

        #region Closed-bar evaluation: divergences & alerts

        private void EvaluateClosedBar(int c, List<Action> actions)
        {
            if (c < 0) return;

            // Divergence detection looks back at the pivot bar, so it runs even if bar c itself
            // has no delta data yet (e.g. right after the first sync).
            DetectDivergence(c, actions);

            if (State == State.Realtime && EnableAlerts && c < barData.Count && barData[c].Visited)
                CheckBarCloseAlerts(c, actions);
        }

        // Pivot at p = c - S is confirmed once bar c has closed.
        private void DetectDivergence(int c, List<Action> actions)
        {
            int s = SwingStrength;
            int p = c - s;
            if (p - s < 0 || p >= barData.Count) return;

            BarDelta pd = barData[p];
            if (!pd.Visited) return;

            var pb = BarsArray[0];
            double ph = pb.GetHigh(p);
            double pl = pb.GetLow(p);
            bool isHigh = true, isLow = true;

            for (int j = p - s; j <= p + s && (isHigh || isLow); j++)
            {
                if (j == p) continue;
                double h = pb.GetHigh(j);
                double l = pb.GetLow(j);
                if (j < p)
                {
                    if (h >= ph) isHigh = false;
                    if (l <= pl) isLow = false;
                }
                else
                {
                    if (h > ph) isHigh = false;
                    if (l < pl) isLow = false;
                }
            }

            if (isHigh)
            {
                SwingPoint sw = new SwingPoint { Idx = p, Price = ph, Cvd = pd.CvdClose, Epoch = pd.Epoch };
                SwingPoint prev = lastSwingHigh;
                // Bearish: price higher high, CVD lower high
                if (prev != null && prev.Epoch == sw.Epoch && p - prev.Idx <= DivMaxBars
                    && sw.Price > prev.Price && sw.Cvd < prev.Cvd)
                    AddDivergence(prev, sw, false, c, actions);
                lastSwingHigh = sw;
            }

            if (isLow)
            {
                SwingPoint sw = new SwingPoint { Idx = p, Price = pl, Cvd = pd.CvdClose, Epoch = pd.Epoch };
                SwingPoint prev = lastSwingLow;
                // Bullish: price lower low, CVD higher low
                if (prev != null && prev.Epoch == sw.Epoch && p - prev.Idx <= DivMaxBars
                    && sw.Price < prev.Price && sw.Cvd > prev.Cvd)
                    AddDivergence(prev, sw, true, c, actions);
                lastSwingLow = sw;
            }
        }

        private void AddDivergence(SwingPoint from, SwingPoint to, bool bullish, int confirmBar, List<Action> actions)
        {
            DivergenceLine dv = new DivergenceLine
            {
                FromIdx = from.Idx, ToIdx = to.Idx,
                FromPrice = from.Price, ToPrice = to.Price,
                FromCvd = from.Cvd, ToCvd = to.Cvd,
                Bullish = bullish
            };
            divergences.Add(dv);

            while (barData.Count <= confirmBar) barData.Add(new BarDelta());
            barData[confirmBar].Div = bullish ? 1 : -1;

            if (ShowDivergences && DivOnPricePanel)
                actions.Add(() => DrawDivergenceOnPrice(dv));

            if (State == State.Realtime && EnableAlerts && AlertOnDivergence)
            {
                string key = bullish ? "DivBull" : "DivBear";
                string msg = bullish ? "Bullish CVD divergence (price lower low, CVD higher low)"
                                     : "Bearish CVD divergence (price higher high, CVD lower high)";
                actions.Add(() => FireAlert(key, msg, confirmBar));
            }
        }

        private void DrawDivergenceOnPrice(DivergenceLine dv)
        {
            int fromAgo = CurrentBar - dv.FromIdx;
            int toAgo   = CurrentBar - dv.ToIdx;
            if (fromAgo < 0 || toAgo < 0) return;

            string tag = "icPAVolumeDeltaCVD_Div_" + dv.ToIdx + (dv.Bullish ? "_B" : "_S");
            Draw.Line(this, tag, false,
                      fromAgo, dv.FromPrice, toAgo, dv.ToPrice,
                      dv.Bullish ? divBullBrush : divBearBrush, DashStyleHelper.Solid, DivLineWidth, true);

            // Cap price-panel drawing objects so long 2000-tick histories stay responsive
            priceDivTags.Enqueue(tag);
            while (priceDivTags.Count > MaxPriceDivLines)
                RemoveDrawObject(priceDivTags.Dequeue());
        }

        private void CheckBarCloseAlerts(int c, List<Action> actions)
        {
            BarDelta d = barData[c];

            if (AlertOnFlip)
            {
                int f = FlipOf(d);
                if (f != 0)
                {
                    string key = f > 0 ? "FlipBull" : "FlipBear";
                    string msg = f > 0 ? "Bullish delta flip" : "Bearish delta flip";
                    actions.Add(() => FireAlert(key, msg, c));
                }
            }

            if (c < 1 || !barData[c - 1].Visited) return;
            BarDelta prev = barData[c - 1];

            if (AlertOnZeroCross && prev.Epoch == d.Epoch)
            {
                if (prev.CvdClose < 0 && d.CvdClose >= 0)
                    actions.Add(() => FireAlert("ZeroUp", "CVD crossed above zero", c));
                else if (prev.CvdClose > 0 && d.CvdClose <= 0)
                    actions.Add(() => FireAlert("ZeroDown", "CVD crossed below zero", c));
            }

            if (AlertOnSignalCross)
            {
                ComputeSignal(c);
                if (prev.HasSignal && d.HasSignal)
                {
                    double before = OutputValue(prev) - prev.Signal;
                    double after  = OutputValue(d) - d.Signal;
                    string what = IndicatorMode == icPAVolumeDeltaCVDMode.VolumeDelta ? "Delta" : "CVD";
                    if (before <= 0 && after > 0)
                        actions.Add(() => FireAlert("SignalUp", what + " crossed above its signal line", c));
                    else if (before >= 0 && after < 0)
                        actions.Add(() => FireAlert("SignalDown", what + " crossed below its signal line", c));
                }
            }
        }

        private void FireAlert(string key, string message, int barIdx)
        {
            string id = Name + "_" + key + "_" + barIdx;
            Alert(id, Priority.Medium, Name + " " + Instrument.FullName + ": " + message,
                  AlertSoundPath(), 5, Brushes.Black, Brushes.White);
        }

        private string AlertSoundPath()
        {
            if (string.IsNullOrWhiteSpace(AlertSound)) return string.Empty;
            if (System.IO.Path.IsPathRooted(AlertSound)) return AlertSound;
            return System.IO.Path.Combine(NinjaTrader.Core.Globals.InstallDir, "sounds", AlertSound);
        }

        #endregion

        #region Bar analytics & publishing

        private double OutputValue(BarDelta d)
        {
            return IndicatorMode == icPAVolumeDeltaCVDMode.VolumeDelta ? d.DeltaClose : d.CvdClose;
        }

        // +1 bullish flip: delta closed positive after dipping below zero to <= -FlipMinExtreme.
        // -1 bearish flip: delta closed negative after rising above zero to >= +FlipMinExtreme.
        private int FlipOf(BarDelta d)
        {
            if (d.DeltaClose > 0 && d.DeltaLow  < 0 && d.DeltaLow  <= -FlipMinExtreme) return 1;
            if (d.DeltaClose < 0 && d.DeltaHigh > 0 && d.DeltaHigh >=  FlipMinExtreme) return -1;
            return 0;
        }

        private System.Windows.Media.Brush PriceBarBrushFor(BarDelta d)
        {
            switch (PriceBarColorMode)
            {
                case icPAVolumeDeltaCVDBarColorMode.DeltaSign:
                    return d.DeltaClose > 0 ? priceBarUp : d.DeltaClose < 0 ? priceBarDown : null;
                case icPAVolumeDeltaCVDBarColorMode.DeltaPercentThreshold:
                    double pct = d.DeltaPercent;
                    return pct >= PriceBarPercentThreshold ? priceBarUp : pct <= -PriceBarPercentThreshold ? priceBarDown : null;
                case icPAVolumeDeltaCVDBarColorMode.DeltaFlips:
                    int f = FlipOf(d);
                    return f > 0 ? priceBarUp : f < 0 ? priceBarDown : null;
            }
            return null;
        }

        private void PublishBar(int barsAgo, int idx)
        {
            if (idx < 0 || idx >= barData.Count || !barData[idx].Visited) return;
            BarDelta d = barData[idx];

            Values[0][barsAgo]    = OutputValue(d);

            buyVolume[barsAgo]    = d.BuyVol;
            sellVolume[barsAgo]   = d.SellVol;
            totalVolume[barsAgo]  = d.TotalVol;
            delta[barsAgo]        = d.DeltaClose;
            deltaHigh[barsAgo]    = d.DeltaHigh;
            deltaLow[barsAgo]     = d.DeltaLow;
            deltaPercent[barsAgo] = d.DeltaPercent;
            deltaFlip[barsAgo]    = FlipOf(d);
            cvd[barsAgo]          = d.CvdClose;
            cvdOpen[barsAgo]      = d.CvdOpen;
            cvdHigh[barsAgo]      = d.CvdOpen + d.DeltaHigh;
            cvdLow[barsAgo]       = d.CvdOpen + d.DeltaLow;
            divergence[barsAgo]   = d.Div;

            if (ShowSignalLine || (EnableAlerts && AlertOnSignalCross))
            {
                ComputeSignal(idx);
                if (ShowSignalLine && d.HasSignal) Values[1][barsAgo] = d.Signal;
            }

            if (PriceBarColorMode != icPAVolumeDeltaCVDBarColorMode.Off)
                BarBrushes[barsAgo] = PriceBarBrushFor(d);
        }

        private void ComputeSignal(int idx)
        {
            BarDelta d = barData[idx];
            double x = OutputValue(d);
            int period = Math.Max(1, SignalPeriod);

            if (SignalType == icPAVolumeDeltaCVDMaType.EMA)
            {
                BarDelta prev = null;
                for (int j = idx - 1; j >= 0; j--)
                {
                    if (barData[j].Visited) { prev = barData[j]; break; }
                }

                d.Signal = (prev == null || !prev.HasSignal)
                    ? x
                    : prev.Signal + (2.0 / (period + 1)) * (x - prev.Signal);
                d.HasSignal = true;
            }
            else
            {
                double sum = 0;
                int n = 0;
                for (int j = idx; j >= 0 && n < period; j--)
                {
                    if (!barData[j].Visited) continue;
                    sum += OutputValue(barData[j]);
                    n++;
                }
                d.HasSignal = n == period;
                d.Signal = n > 0 ? sum / n : 0;
            }
        }

        private void PrintDiagnosticSummary()
        {
            lock (dataLock)
            {
                long total = cntQuote + cntInsideSpread + cntNoQuote;
                string series = Instrument.FullName + " " + BarsArray[0].BarsPeriod.ToString();

                if (total == 0)
                {
                    Print(Name + " [" + series + "]: no historical trades were mapped. Check that the chart loads data by Days and that 1-tick data is available.");
                    return;
                }

                Print(string.Format("{0} [{1}]: {2:N0} historical trades | bid/ask: {3:P1} | inside spread (tick rule): {4:P1} | no valid quotes (tick rule): {5:P1} | unclassified: {6:N0} | mapping: {7} | corrections: {8:N0} | divergences: {9:N0}",
                    Name, series, total,
                    (double)cntQuote / total,
                    (double)cntInsideSpread / total,
                    (double)cntNoQuote / total,
                    cntUnclassified,
                    useTimeMapping ? "time" : "volume",
                    cntMapCorrections,
                    divergences != null ? divergences.Count : 0));

                if (cntNoQuote > total / 2)
                    Print(Name + ": most historical trades had no valid bid/ask. Your data provider may not supply historical bid/ask, so historical delta uses the tick rule and is approximate. Real-time delta is unaffected.");
            }
        }

        private static System.Windows.Media.Brush FrozenCopy(System.Windows.Media.Brush b)
        {
            if (b == null || b.IsFrozen) return b;
            System.Windows.Media.Brush c = b.Clone();
            c.Freeze();
            return c;
        }

        private static System.Windows.Media.Brush MakeBrush(byte r, byte g, byte b)
        {
            System.Windows.Media.SolidColorBrush br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        #endregion

        #region Rendering

        public override void OnRenderTargetChanged()
        {
            DisposeDxResources();
            if (RenderTarget == null) return;

            if (UpColor != null)         dxUp       = UpColor.ToDxBrush(RenderTarget);
            if (DownColor != null)       dxDown     = DownColor.ToDxBrush(RenderTarget);
            if (OutlineColor != null)    dxOutline  = OutlineColor.ToDxBrush(RenderTarget);
            if (ZeroLineColor != null)   dxZero     = ZeroLineColor.ToDxBrush(RenderTarget);
            if (FlipUpColor != null)     dxFlipUp   = FlipUpColor.ToDxBrush(RenderTarget);
            if (FlipDownColor != null)   dxFlipDown = FlipDownColor.ToDxBrush(RenderTarget);
            if (BullishDivColor != null) dxDivBull  = BullishDivColor.ToDxBrush(RenderTarget);
            if (BearishDivColor != null) dxDivBear  = BearishDivColor.ToDxBrush(RenderTarget);
            dxNeutral = Brushes.Gray.ToDxBrush(RenderTarget);

            // Unit flip triangle: tip at (0,0), base at y = 1.5, half-width 1.
            // Positioned, scaled and mirrored per marker through RenderTarget.Transform.
            flipTriangle = new SharpDX.Direct2D1.PathGeometry(NinjaTrader.Core.Globals.D2DFactory);
            using (SharpDX.Direct2D1.GeometrySink sink = flipTriangle.Open())
            {
                sink.BeginFigure(new SharpDX.Vector2(0f, 0f), SharpDX.Direct2D1.FigureBegin.Filled);
                sink.AddLine(new SharpDX.Vector2(-1f, 1.5f));
                sink.AddLine(new SharpDX.Vector2(1f, 1.5f));
                sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                sink.Close();
            }

            if (LabelFont != null)
            {
                labelTextFormat = LabelFont.ToDirectWriteTextFormat();
                labelTextFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
                labelTextFormat.WordWrapping  = SharpDX.DirectWrite.WordWrapping.NoWrap;
            }
            measuredLabel = null;
        }

        private void DisposeDxResources()
        {
            if (dxUp != null)       { dxUp.Dispose();       dxUp = null; }
            if (dxDown != null)     { dxDown.Dispose();     dxDown = null; }
            if (dxOutline != null)  { dxOutline.Dispose();  dxOutline = null; }
            if (dxZero != null)     { dxZero.Dispose();     dxZero = null; }
            if (dxFlipUp != null)   { dxFlipUp.Dispose();   dxFlipUp = null; }
            if (dxFlipDown != null) { dxFlipDown.Dispose(); dxFlipDown = null; }
            if (dxNeutral != null)  { dxNeutral.Dispose();  dxNeutral = null; }
            if (dxDivBull != null)  { dxDivBull.Dispose();  dxDivBull = null; }
            if (dxDivBear != null)  { dxDivBear.Dispose();  dxDivBear = null; }
            if (flipTriangle != null)    { flipTriangle.Dispose();    flipTriangle = null; }
            if (labelTextFormat != null) { labelTextFormat.Dispose(); labelTextFormat = null; }
            measuredLabel = null;
        }

        private bool CandleModeActive
        {
            get { return IndicatorMode == icPAVolumeDeltaCVDMode.VolumeDelta || CVDStyle == icPAVolumeDeltaCVDDrawStyle.Candlestick; }
        }

        private int LabelRowCount
        {
            get
            {
                switch (LabelMode)
                {
                    case icPAVolumeDeltaCVDLabelMode.Delta:
                    case icPAVolumeDeltaCVDLabelMode.DeltaPercent:    return 1;
                    case icPAVolumeDeltaCVDLabelMode.DeltaAndPercent:
                    case icPAVolumeDeltaCVDLabelMode.BuySellVolume:   return 2;
                }
                return 0;
            }
        }

        public override void OnCalculateMinMax()
        {
            if (ChartBars == null || barData == null) return;

            bool vdMode   = IndicatorMode == icPAVolumeDeltaCVDMode.VolumeDelta;
            bool lineMode = !vdMode && CVDStyle == icPAVolumeDeltaCVDDrawStyle.Line;

            double calcMin = double.MaxValue;
            double calcMax = double.MinValue;

            lock (dataLock)
            {
                int from = Math.Max(0, ChartBars.FromIndex);
                int to   = Math.Min(ChartBars.ToIndex, barData.Count - 1);

                for (int i = from; i <= to; i++)
                {
                    BarDelta d = barData[i];
                    if (!d.Visited) continue;

                    double hi, lo;
                    if (vdMode)        { hi = d.DeltaHigh; lo = d.DeltaLow; }
                    else if (lineMode) { hi = d.CvdClose;  lo = d.CvdClose; }
                    else               { hi = d.CvdOpen + d.DeltaHigh; lo = d.CvdOpen + d.DeltaLow; }

                    // Keep the signal line inside the scale (an EMA can sit outside the visible bars' range)
                    if (ShowSignalLine && d.HasSignal)
                    {
                        if (d.Signal > hi) hi = d.Signal;
                        if (d.Signal < lo) lo = d.Signal;
                    }

                    if (hi > calcMax) calcMax = hi;
                    if (lo < calcMin) calcMin = lo;
                }
            }

            if (calcMin != double.MaxValue && calcMax != double.MinValue)
            {
                double range = calcMax - calcMin;
                if (range == 0) range = 1;

                // Extra room for flip markers above/below candles and for the label rows at the bottom
                double markerPad = (ShowDeltaFlips && CandleModeActive) ? 0.06 : 0;
                double topPad    = 0.05 + markerPad;
                double bottomPad = 0.05 + markerPad + 0.10 * LabelRowCount;

                MaxValue = calcMax + range * topPad;
                MinValue = calcMin - range * bottomPad;
            }
            else
            {
                MinValue = -100;
                MaxValue = 100;
            }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (barData != null && ChartBars != null && RenderTarget != null)
            {
                SharpDX.Direct2D1.AntialiasMode oldMode = RenderTarget.AntialiasMode;
                RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;
                try
                {
                    // Zero line first so candles and plots draw on top of it
                    if (ShowZeroLine && dxZero != null && chartScale.MaxValue >= 0 && chartScale.MinValue <= 0)
                    {
                        float y0 = chartScale.GetYByValue(0);
                        RenderTarget.DrawLine(new SharpDX.Vector2(ChartPanel.X, y0),
                                              new SharpDX.Vector2(ChartPanel.X + ChartPanel.W, y0),
                                              dxZero, ZeroLineWidth);
                    }

                    bool vdMode = IndicatorMode == icPAVolumeDeltaCVDMode.VolumeDelta;

                    if (CandleModeActive && dxUp != null && dxDown != null && dxOutline != null)
                        DrawCandles(chartControl, chartScale, vdMode);

                    RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;

                    if (ShowDeltaFlips && CandleModeActive && dxFlipUp != null && dxFlipDown != null && flipTriangle != null)
                        DrawFlipMarkers(chartControl, chartScale, vdMode);

                    // Divergence lines in the panel only make sense when the panel shows CVD
                    if (ShowDivergences && DivOnIndicatorPanel && !vdMode && dxDivBull != null && dxDivBear != null)
                        DrawPanelDivergences(chartControl, chartScale);

                    if (LabelMode != icPAVolumeDeltaCVDLabelMode.None && LabelFont != null && labelTextFormat != null
                        && dxUp != null && dxDown != null && dxNeutral != null)
                        DrawLabels(chartControl);
                }
                finally
                {
                    RenderTarget.AntialiasMode = oldMode;
                }
            }

            // Plots (CVD line, signal line) on top of the candles
            base.OnRender(chartControl, chartScale);
        }

        private void DrawCandles(ChartControl chartControl, ChartScale chartScale, bool vdMode)
        {
            float barPaintWidth = Math.Max(3, chartControl.GetBarPaintWidth(ChartBars));
            float halfWidth = barPaintWidth / 2f;

            lock (dataLock)
            {
                int from = Math.Max(0, ChartBars.FromIndex);
                int to   = Math.Min(ChartBars.ToIndex, barData.Count - 1);

                for (int i = from; i <= to; i++)
                {
                    BarDelta d = barData[i];
                    if (!d.Visited) continue;

                    double baseVal = vdMode ? 0 : d.CvdOpen;
                    double open  = baseVal;
                    double high  = baseVal + d.DeltaHigh;
                    double low   = baseVal + d.DeltaLow;
                    double close = baseVal + d.DeltaClose;

                    float x      = chartControl.GetXByBarIndex(ChartBars, i);
                    float yOpen  = chartScale.GetYByValue(open);
                    float yHigh  = chartScale.GetYByValue(high);
                    float yLow   = chartScale.GetYByValue(low);
                    float yClose = chartScale.GetYByValue(close);

                    SharpDX.Direct2D1.Brush bodyBrush = close >= open ? dxUp : dxDown;

                    RenderTarget.DrawLine(new SharpDX.Vector2(x, yHigh), new SharpDX.Vector2(x, yLow), dxOutline, OutlineWidth);

                    float bodyTop    = Math.Min(yOpen, yClose);
                    float bodyBottom = Math.Max(yOpen, yClose);

                    if (bodyBottom - bodyTop <= 0.5f)
                    {
                        RenderTarget.DrawLine(new SharpDX.Vector2(x - halfWidth, bodyTop), new SharpDX.Vector2(x + halfWidth, bodyTop), dxOutline, OutlineWidth);
                    }
                    else
                    {
                        SharpDX.RectangleF rect = new SharpDX.RectangleF(x - halfWidth, bodyTop, barPaintWidth, bodyBottom - bodyTop);
                        RenderTarget.FillRectangle(rect, bodyBrush);
                        RenderTarget.DrawRectangle(rect, dxOutline, OutlineWidth);
                    }
                }
            }
        }

        private void DrawFlipMarkers(ChartControl chartControl, ChartScale chartScale, bool vdMode)
        {
            float size = Math.Max(4f, Math.Min(10f, chartControl.GetBarPaintWidth(ChartBars) / 2f + 1f));
            const float gap = 3f;

            SharpDX.Matrix3x2 oldTransform = RenderTarget.Transform;
            try
            {
                lock (dataLock)
                {
                    int from = Math.Max(0, ChartBars.FromIndex);
                    int to   = Math.Min(ChartBars.ToIndex, barData.Count - 1);

                    for (int i = from; i <= to; i++)
                    {
                        BarDelta d = barData[i];
                        if (!d.Visited) continue;

                        int flip = FlipOf(d);
                        if (flip == 0) continue;

                        double baseVal = vdMode ? 0 : d.CvdOpen;
                        float x = chartControl.GetXByBarIndex(ChartBars, i);

                        if (flip > 0)
                        {
                            // Up-pointing triangle below the candle low
                            float yTip = chartScale.GetYByValue(baseVal + d.DeltaLow) + gap;
                            RenderTarget.Transform = SharpDX.Matrix3x2.Scaling(size, size)
                                                   * SharpDX.Matrix3x2.Translation(x, yTip) * oldTransform;
                            RenderTarget.FillGeometry(flipTriangle, dxFlipUp);
                        }
                        else
                        {
                            // Down-pointing triangle above the candle high (unit triangle mirrored vertically)
                            float yTip = chartScale.GetYByValue(baseVal + d.DeltaHigh) - gap;
                            RenderTarget.Transform = SharpDX.Matrix3x2.Scaling(size, -size)
                                                   * SharpDX.Matrix3x2.Translation(x, yTip) * oldTransform;
                            RenderTarget.FillGeometry(flipTriangle, dxFlipDown);
                        }
                    }
                }
            }
            finally
            {
                RenderTarget.Transform = oldTransform;
            }
        }

        private void DrawPanelDivergences(ChartControl chartControl, ChartScale chartScale)
        {
            lock (dataLock)
            {
                int from = ChartBars.FromIndex;
                int to   = ChartBars.ToIndex;

                // Divergences are appended in pivot order (ToIdx never decreases), so walk back
                // from the newest and stop once a line ends before the visible range.
                for (int k = divergences.Count - 1; k >= 0; k--)
                {
                    DivergenceLine dv = divergences[k];
                    if (dv.ToIdx < from) break;
                    if (dv.FromIdx > to) continue;

                    float x1 = chartControl.GetXByBarIndex(ChartBars, dv.FromIdx);
                    float x2 = chartControl.GetXByBarIndex(ChartBars, dv.ToIdx);
                    float y1 = chartScale.GetYByValue(dv.FromCvd);
                    float y2 = chartScale.GetYByValue(dv.ToCvd);

                    RenderTarget.DrawLine(new SharpDX.Vector2(x1, y1), new SharpDX.Vector2(x2, y2),
                                          dv.Bullish ? dxDivBull : dxDivBear, DivLineWidth);
                }
            }
        }

        private void DrawLabels(ChartControl chartControl)
        {
            int rows = LabelRowCount;
            if (rows == 0) return;

            float barDistance = chartControl.Properties.BarDistance;
            float rowHeight = (float)LabelFont.Size * 1.5f;
            float bottom = ChartPanel.Y + ChartPanel.H - 2f;

            lock (dataLock)
            {
                int from = Math.Max(0, ChartBars.FromIndex);
                int to   = Math.Min(ChartBars.ToIndex, barData.Count - 1);

                // Only draw labels if the longest one fits between bars. Label strings are cached
                // per bar and rebuilt only when that bar's volumes change.
                string longest = string.Empty;
                for (int i = from; i <= to; i++)
                {
                    BarDelta d = barData[i];
                    if (!d.Visited) continue;
                    UpdateLabelCache(d);
                    if (d.LabelRows > 0 && d.LabelText0.Length > longest.Length) longest = d.LabelText0;
                    if (d.LabelRows > 1 && d.LabelText1.Length > longest.Length) longest = d.LabelText1;
                }
                if (longest.Length == 0) return;

                // Measure only when the longest label text changes
                if (!string.Equals(longest, measuredLabel, StringComparison.Ordinal))
                {
                    using (SharpDX.DirectWrite.TextLayout layout = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, longest, labelTextFormat, 1000f, rowHeight))
                    {
                        measuredLabelWidth = layout.Metrics.Width;
                    }
                    measuredLabel = longest;
                }
                if (measuredLabelWidth + 2f > barDistance) return;

                for (int i = from; i <= to; i++)
                {
                    BarDelta d = barData[i];
                    if (!d.Visited || d.LabelRows == 0) continue;

                    float x = chartControl.GetXByBarIndex(ChartBars, i);
                    int n = d.LabelRows;

                    for (int r = 0; r < n; r++)
                    {
                        string text = r == 0 ? d.LabelText0 : d.LabelText1;
                        int sign    = r == 0 ? d.LabelSign0 : d.LabelSign1;
                        float y = bottom - (n - r) * rowHeight;
                        SharpDX.RectangleF rect = new SharpDX.RectangleF(x - barDistance / 2f, y, barDistance, rowHeight);
                        SharpDX.Direct2D1.Brush b = sign > 0 ? dxUp : sign < 0 ? dxDown : dxNeutral;
                        RenderTarget.DrawText(text, labelTextFormat, rect, b);
                    }
                }
            }
        }

        private void UpdateLabelCache(BarDelta d)
        {
            // LabelKeyBuy starts as NaN, which never compares equal, so the first call always builds
            if (d.LabelKeyBuy == d.BuyVol && d.LabelKeySell == d.SellVol && d.LabelKeyTotal == d.TotalVol)
                return;

            d.LabelKeyBuy   = d.BuyVol;
            d.LabelKeySell  = d.SellVol;
            d.LabelKeyTotal = d.TotalVol;

            double pct = d.DeltaPercent;
            switch (LabelMode)
            {
                case icPAVolumeDeltaCVDLabelMode.Delta:
                    d.LabelText0 = FormatSigned(d.DeltaClose); d.LabelSign0 = Math.Sign(d.DeltaClose);
                    d.LabelRows = 1;
                    return;
                case icPAVolumeDeltaCVDLabelMode.DeltaPercent:
                    d.LabelText0 = pct.ToString("+0;-0;0") + "%"; d.LabelSign0 = Math.Sign(pct);
                    d.LabelRows = 1;
                    return;
                case icPAVolumeDeltaCVDLabelMode.DeltaAndPercent:
                    d.LabelText0 = FormatSigned(d.DeltaClose); d.LabelSign0 = Math.Sign(d.DeltaClose);
                    d.LabelText1 = pct.ToString("+0;-0;0") + "%"; d.LabelSign1 = Math.Sign(pct);
                    d.LabelRows = 2;
                    return;
                case icPAVolumeDeltaCVDLabelMode.BuySellVolume:
                    d.LabelText0 = FormatVolume(d.BuyVol);  d.LabelSign0 = 1;
                    d.LabelText1 = FormatVolume(d.SellVol); d.LabelSign1 = -1;
                    d.LabelRows = 2;
                    return;
            }
            d.LabelRows = 0;
        }

        private static string FormatVolume(double v)
        {
            double a = Math.Abs(v);
            if (a >= 1000000) return (v / 1000000).ToString("0.#") + "M";
            if (a >= 10000)   return (v / 1000).ToString("0.#") + "K";
            return v.ToString("#,0");
        }

        private static string FormatSigned(double v)
        {
            string s = FormatVolume(Math.Abs(v));
            return v > 0 ? "+" + s : v < 0 ? "-" + s : "0";
        }

        #endregion

        #region Properties

        // ---------- 1. General ----------
        [NinjaScriptProperty]
        [Display(Name="Indicator Mode", Order=1, GroupName="1. General", Description="Volume Delta: delta per bar. Cumulative Volume Delta: running total of delta since the last reset.")]
        public icPAVolumeDeltaCVDMode IndicatorMode { get; set; }

        [Display(Name="CVD Display Style", Order=2, GroupName="1. General", Description="Draw CVD as candlesticks (with intrabar high/low) or as a line. Ignored in Volume Delta mode.")]
        public icPAVolumeDeltaCVDDrawStyle CVDStyle { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name="Min Trade Size", Order=3, GroupName="1. General", Description="Only trades of at least this many contracts count toward delta and CVD. 1 = all trades.")]
        public int MinTradeSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name="CVD Reset Mode", Order=4, GroupName="1. General", Description="When CVD returns to zero: every session, daily at a set time, weekly, once at an anchor time, or never.")]
        public icPAVolumeDeltaCVDResetMode ResetMode { get; set; }

        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name="CVD Reset Time", Order=5, GroupName="1. General", Description="Time of day for 'Daily At Time' (weekdays only), in the Reset Time Zone.")]
        public DateTime ResetTime { get; set; }

        [Display(Name="CVD Reset Time Zone", Order=6, GroupName="1. General", Description="Time zone for Reset Time and Anchor Time. Daylight saving is handled automatically.")]
        public icPAVolumeDeltaCVDTimeZone ResetTimeZone { get; set; }

        [Display(Name="CVD Anchor Time", Order=7, GroupName="1. General", Description="Date and time for 'Anchor' reset mode, in the Reset Time Zone.")]
        public DateTime AnchorTime { get; set; }

        [Display(Name="Signal Type", Order=8, GroupName="1. General", Description="Exponential (EMA) or simple (SMA) moving average for the signal line and signal cross alerts.")]
        public icPAVolumeDeltaCVDMaType SignalType { get; set; }

        [Range(1, 1000)]
        [Display(Name="Signal Period", Order=9, GroupName="1. General", Description="Number of bars in the signal line moving average.")]
        public int SignalPeriod { get; set; }

        [Display(Name="Print Diagnostics", Order=10, GroupName="1. General", Description="After loading, print data-quality stats (trade classification, mapping) to the Output window.")]
        public bool PrintDiagnostics { get; set; }

        // ---------- 2. Visuals ----------
        [XmlIgnore]
        [Display(Name="Candle Up Color", Order=1, GroupName="2. Visuals", Description="Body color when the bar closes above its open. Also used for positive labels.")]
        public System.Windows.Media.Brush UpColor { get; set; }

        [Browsable(false)]
        public string UpColorSerializable
        {
            get { return Serialize.BrushToString(UpColor); }
            set { UpColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name="Candle Down Color", Order=2, GroupName="2. Visuals", Description="Body color when the bar closes below its open. Also used for negative labels.")]
        public System.Windows.Media.Brush DownColor { get; set; }

        [Browsable(false)]
        public string DownColorSerializable
        {
            get { return Serialize.BrushToString(DownColor); }
            set { DownColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name="Candle Outline Color", Order=3, GroupName="2. Visuals", Description="Color of wicks and body outlines.")]
        public System.Windows.Media.Brush OutlineColor { get; set; }

        [Browsable(false)]
        public string OutlineColorSerializable
        {
            get { return Serialize.BrushToString(OutlineColor); }
            set { OutlineColor = Serialize.StringToBrush(value); }
        }

        [Range(1, 10)]
        [Display(Name="Candle Outline Width", Order=4, GroupName="2. Visuals", Description="Width of wicks and body outlines.")]
        public int OutlineWidth { get; set; }

        [XmlIgnore]
        [Display(Name="CVD Line Color", Order=5, GroupName="2. Visuals", Description="Used when CVD Display Style is Line.")]
        public System.Windows.Media.Brush CvdLineColor { get; set; }

        [Browsable(false)]
        public string CvdLineColorSerializable
        {
            get { return Serialize.BrushToString(CvdLineColor); }
            set { CvdLineColor = Serialize.StringToBrush(value); }
        }

        [Range(1, 10)]
        [Display(Name="CVD Line Width", Order=6, GroupName="2. Visuals", Description="Used when CVD Display Style is Line.")]
        public int CvdLineWidth { get; set; }

        [Display(Name="CVD Line Style", Order=7, GroupName="2. Visuals", Description="Used when CVD Display Style is Line.")]
        public DashStyleHelper CvdDashStyle { get; set; }

        [Display(Name="Show Signal Line", Order=8, GroupName="2. Visuals", Description="Moving average of the displayed Delta or CVD. Set its color and width under Plots > Signal.")]
        public bool ShowSignalLine { get; set; }

        [Display(Name="Show Zero Line", Order=9, GroupName="2. Visuals", Description="Horizontal line at zero, drawn behind the candles.")]
        public bool ShowZeroLine { get; set; }

        [XmlIgnore]
        [Display(Name="Zero Line Color", Order=10, GroupName="2. Visuals", Description="Color of the zero line.")]
        public System.Windows.Media.Brush ZeroLineColor { get; set; }

        [Browsable(false)]
        public string ZeroLineColorSerializable
        {
            get { return Serialize.BrushToString(ZeroLineColor); }
            set { ZeroLineColor = Serialize.StringToBrush(value); }
        }

        [Range(1, 10)]
        [Display(Name="Zero Line Width", Order=11, GroupName="2. Visuals", Description="Width of the zero line.")]
        public int ZeroLineWidth { get; set; }

        [Display(Name="Show Delta Flips", Order=12, GroupName="2. Visuals", Description="Mark candles where delta crossed zero within the bar and closed on the other side.")]
        public bool ShowDeltaFlips { get; set; }

        [XmlIgnore]
        [Display(Name="Bullish Flip Color", Order=13, GroupName="2. Visuals", Description="Marker below candles that flipped from negative to positive delta.")]
        public System.Windows.Media.Brush FlipUpColor { get; set; }

        [Browsable(false)]
        public string FlipUpColorSerializable
        {
            get { return Serialize.BrushToString(FlipUpColor); }
            set { FlipUpColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name="Bearish Flip Color", Order=14, GroupName="2. Visuals", Description="Marker above candles that flipped from positive to negative delta.")]
        public System.Windows.Media.Brush FlipDownColor { get; set; }

        [Browsable(false)]
        public string FlipDownColorSerializable
        {
            get { return Serialize.BrushToString(FlipDownColor); }
            set { FlipDownColor = Serialize.StringToBrush(value); }
        }

        [Display(Name="Label Content", Order=15, GroupName="2. Visuals", Description="Text shown under each bar. Hidden automatically when bars are too close to fit it.")]
        public icPAVolumeDeltaCVDLabelMode LabelMode { get; set; }

        [Display(Name="Label Font", Order=16, GroupName="2. Visuals", Description="Font for the bar labels.")]
        public NinjaTrader.Gui.Tools.SimpleFont LabelFont { get; set; }

        [Display(Name="Color Price Bars", Order=17, GroupName="2. Visuals", Description="Color the main chart's bars by delta sign, by delta % beyond the threshold, or on flip bars only.")]
        public icPAVolumeDeltaCVDBarColorMode PriceBarColorMode { get; set; }

        [XmlIgnore]
        [Display(Name="Price Bar Up Color", Order=18, GroupName="2. Visuals", Description="Price bar color for positive delta conditions.")]
        public System.Windows.Media.Brush PriceBarUpColor { get; set; }

        [Browsable(false)]
        public string PriceBarUpColorSerializable
        {
            get { return Serialize.BrushToString(PriceBarUpColor); }
            set { PriceBarUpColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name="Price Bar Down Color", Order=19, GroupName="2. Visuals", Description="Price bar color for negative delta conditions.")]
        public System.Windows.Media.Brush PriceBarDownColor { get; set; }

        [Browsable(false)]
        public string PriceBarDownColorSerializable
        {
            get { return Serialize.BrushToString(PriceBarDownColor); }
            set { PriceBarDownColor = Serialize.StringToBrush(value); }
        }

        [Display(Name="Show Divergences", Order=20, GroupName="2. Visuals", Description="Draw divergences between price swings and CVD. Detection always runs for alerts and strategies.")]
        public bool ShowDivergences { get; set; }

        [Display(Name="Divergences On Price Panel", Order=21, GroupName="2. Visuals", Description="Draw divergence lines on the price panel (the most recent 500 are kept).")]
        public bool DivOnPricePanel { get; set; }

        [Display(Name="Divergences On Indicator Panel", Order=22, GroupName="2. Visuals", Description="Draw divergence lines in this panel. Only drawn when the panel shows CVD.")]
        public bool DivOnIndicatorPanel { get; set; }

        [XmlIgnore]
        [Display(Name="Bullish Divergence Color", Order=23, GroupName="2. Visuals", Description="Price lower low with CVD higher low.")]
        public System.Windows.Media.Brush BullishDivColor { get; set; }

        [Browsable(false)]
        public string BullishDivColorSerializable
        {
            get { return Serialize.BrushToString(BullishDivColor); }
            set { BullishDivColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name="Bearish Divergence Color", Order=24, GroupName="2. Visuals", Description="Price higher high with CVD lower high.")]
        public System.Windows.Media.Brush BearishDivColor { get; set; }

        [Browsable(false)]
        public string BearishDivColorSerializable
        {
            get { return Serialize.BrushToString(BearishDivColor); }
            set { BearishDivColor = Serialize.StringToBrush(value); }
        }

        [Range(1, 10)]
        [Display(Name="Divergence Line Width", Order=25, GroupName="2. Visuals", Description="Width of divergence lines on both panels.")]
        public int DivLineWidth { get; set; }

        // ---------- 3. Offsets & Thresholds ----------
        [Range(0, double.MaxValue)]
        [Display(Name="Flip Min Extreme", Order=1, GroupName="3. Offsets & Thresholds", Description="How far (in contracts) delta must reach on the opposite side before a flip counts. Scale to instrument and bar size.")]
        public double FlipMinExtreme { get; set; }

        [Range(0, 100)]
        [Display(Name="Delta % Threshold", Order=2, GroupName="3. Offsets & Thresholds", Description="Minimum delta, as % of the bar's volume, for the 'Delta Percent Threshold' price bar coloring.")]
        public double PriceBarPercentThreshold { get; set; }

        [Range(1, 50)]
        [Display(Name="Swing Strength", Order=3, GroupName="3. Offsets & Thresholds", Description="Bars needed on each side to confirm a swing. Divergences appear this many bars after the swing.")]
        public int SwingStrength { get; set; }

        [Range(2, 1000)]
        [Display(Name="Max Bars Between Swings", Order=4, GroupName="3. Offsets & Thresholds", Description="Swings further apart than this are not compared for divergences.")]
        public int DivMaxBars { get; set; }

        // ---------- 4. Alerts ----------
        [Display(Name="Enable Alerts", Order=1, GroupName="4. Alerts", Description="Real-time only. Alerts appear in the Alerts Log and play the Alert Sound.")]
        public bool EnableAlerts { get; set; }

        [Display(Name="On Divergence", Order=2, GroupName="4. Alerts", Description="When a divergence is confirmed.")]
        public bool AlertOnDivergence { get; set; }

        [Display(Name="On Delta Flip", Order=3, GroupName="4. Alerts", Description="At bar close.")]
        public bool AlertOnFlip { get; set; }

        [Display(Name="On CVD Zero Cross", Order=4, GroupName="4. Alerts", Description="At bar close. The drop to zero caused by a reset is ignored.")]
        public bool AlertOnZeroCross { get; set; }

        [Display(Name="On Signal Line Cross", Order=5, GroupName="4. Alerts", Description="At bar close. Works even if the signal line is hidden.")]
        public bool AlertOnSignalCross { get; set; }

        [Display(Name="On Bar Delta Threshold", Order=6, GroupName="4. Alerts", Description="Intrabar, at most once per bar, when |delta| reaches the threshold.")]
        public bool AlertOnDeltaThreshold { get; set; }

        [Range(1, double.MaxValue)]
        [Display(Name="Bar Delta Threshold", Order=7, GroupName="4. Alerts", Description="Absolute bar delta, in contracts.")]
        public double AlertDeltaThreshold { get; set; }

        [Display(Name="Alert Sound", Order=8, GroupName="4. Alerts", Description="File in NinjaTrader's sounds folder (e.g. Alert2.wav) or a full path. Empty = no sound.")]
        public string AlertSound { get; set; }

        // ---------- Public data series (for strategies / other indicators) ----------
        // Bars before the first full session are never set; check IsValidDataPoint before use.
        [Browsable(false)][XmlIgnore]
        public Series<double> DeltaOutput { get { return Values[0]; } }

        [Browsable(false)][XmlIgnore]
        public Series<double> Signal { get { return Values[1]; } }

        [Browsable(false)][XmlIgnore]
        public Series<double> BuyVolume { get { Update(); return buyVolume; } }

        [Browsable(false)][XmlIgnore]
        public Series<double> SellVolume { get { Update(); return sellVolume; } }

        [Browsable(false)][XmlIgnore]
        public Series<double> TotalVolume { get { Update(); return totalVolume; } }

        [Browsable(false)][XmlIgnore]
        public Series<double> Delta { get { Update(); return delta; } }

        [Browsable(false)][XmlIgnore]
        public Series<double> DeltaHigh { get { Update(); return deltaHigh; } }

        [Browsable(false)][XmlIgnore]
        public Series<double> DeltaLow { get { Update(); return deltaLow; } }

        [Browsable(false)][XmlIgnore]
        public Series<double> DeltaPercent { get { Update(); return deltaPercent; } }

        /// <summary>+1 bullish flip, -1 bearish flip, 0 none.</summary>
        [Browsable(false)][XmlIgnore]
        public Series<double> DeltaFlip { get { Update(); return deltaFlip; } }

        [Browsable(false)][XmlIgnore]
        public Series<double> Cvd { get { Update(); return cvd; } }

        [Browsable(false)][XmlIgnore]
        public Series<double> CvdOpen { get { Update(); return cvdOpen; } }

        [Browsable(false)][XmlIgnore]
        public Series<double> CvdHigh { get { Update(); return cvdHigh; } }

        [Browsable(false)][XmlIgnore]
        public Series<double> CvdLow { get { Update(); return cvdLow; } }

        /// <summary>+1 bullish / -1 bearish divergence, set on the bar where it was confirmed; 0 otherwise.</summary>
        [Browsable(false)][XmlIgnore]
        public Series<double> DivergenceSignal { get { Update(); return divergence; } }

        #endregion
    }
}