# FVG Reaction + Liquidity Sweep — ATAS indicator

A custom indicator for the [ATAS](https://atas.net) platform that:

1. **reads candlestick patterns**: the ones on [TraderLion's cheat sheet](https://traderlion.com/technical-analysis/candlestick-patterns-cheat-sheet/)
   plus ten stronger, confirmed ones;
2. uses those patterns to tell whether price reacted **bullish or bearish off a Fair Value Gap**;
3. finds **the price inside each bar where the most contracts were filled**, marks whether the
   resting bids or offers were filled there, and reads the reaction that followed (bullish or bearish);
4. shows the **resting limit orders of 70+ contracts still waiting in the order book**, live
   from Level 2, and tells filled orders from pulled ones;
5. **stops watching a gap once it has been used or filled**;
6. turns reactions and liquidity sweeps into **BUY / SHORT signals with the odds of each way the
   trade can end**: the take profit (80 ticks), the break-even stop (the stop moves to +20 ticks
   once the trade is 40 ticks in profit) or the stop loss (80 ticks).

![Layout preview](docs/preview.png)

*Layout preview produced by the test harness from synthetic data, with a simulated order book and
the mouse over a big fill. It is not an ATAS screenshot: ATAS draws the candles and arrows itself,
with its own fonts and theme.*

## What it draws

| On the chart | Meaning |
|---|---|
| Teal / red boxes reaching the right edge | Bullish / bearish Fair Value Gaps still being watched, with a dashed line at their middle (50%) |
| Small ▲ / ▼ | A bullish / bearish reaction off a gap that did not become a signal on the chart. Hover it for the pattern (or switch on *Show reaction labels* to name it, e.g. `Hammer`) |
| Bubbles | Big fills: the price inside a bar where the most contracts traded (live, also a big resting order the order book saw filled). Bigger = more contracts. **Green** = the reaction after it was bullish, **red** = bearish, **gray** = no clear reaction; faint while it is still being watched |
| White ring around a bubble | The order book saw a resting order of 70+ contracts filled right there |
| Blue / orange bands with a size tag | Resting **bids / offers of 70+ contracts** still waiting in the order book, from the bar where they appeared; the tag at the right edge shows their size. The bigger the order, the stronger the color. A filled one stops, faintly, where it was filled |
| Dotted line ending in a dot | A liquidity sweep: from the swing high / low that held the stops to the bar that ran them |
| **Large arrows + card** | **BUY / SHORT signal** of the open trade: the setup, the odds of TP / break-even / SL and the expected ticks |
| Large arrows + small chip | A signal whose trade has ended, with its result: `TP +80t`, `BE +20t`, `SL -80t` or `EXP` |
| Shaded boxes | The open trade: entry → TP shaded green, entry → SL shaded red, amber dotted line at the break-even trigger (+40 ticks), solid amber once the stop has moved to +20, price tags at the right end. Trades that have ended leave a faint box |
| Panel | Results, the labels' track record, the fills and resting orders so far, and the live odds of the open trade |

Hover the mouse over a card or chip, a bubble, a reaction marker or an order band for the details.
Gaps that are no longer watched can also be kept as faint boxes (*Show used / filled zones*).

## Fair Value Gaps

A gap is the classic three-candle imbalance: a **bullish** gap when the third candle's low is
above the first candle's high, a **bearish** one when the third candle's high is below the first
candle's low, by at least *Min FVG Size* ticks (4).

A gap is watched from the candle after the one that completes it, until one of these happens,
and then it is never used again:

* **Used**: a reaction formed at it (below). The first reaction uses it.
* **Filled**: by default when price reaches its far edge (a bullish gap's bottom, a bearish
  gap's top). *FVG is filled when* can instead wait for a candle to close beyond it, or
  count the gap as filled once price reaches its middle (50%). When a reaction and a fill come
  on the same bar (a candle that stabs through the whole gap and closes back out of it with a
  pattern), the reaction counts first.
* **Expired**: nothing used or filled it within *Zone Max Age* bars (150).

Gaps that stop being watched disappear from the chart. Switch *Show used / filled zones* on to
keep a faint box for each, up to the bar where it ended.

### Bullish or bearish reaction?

A reaction is a **candlestick pattern that formed at the gap**: it completes on a closed bar and
one of its candles traded into the gap. A three-candle pattern counts when its first candle
touched the gap, for example. The pattern decides which way the reaction goes:

* a **bullish** pattern (hammer, engulfing, morning star...) is a bullish reaction, a **bearish**
  pattern is a bearish one, at either kind of gap. A bullish gap that holds gives a bullish
  reaction; a bearish pattern at a bullish gap means it failed.
* with *Require reaction close beyond zone* on (the default), a bullish reaction has to close
  above the gap and a bearish one below it, not just form a pattern inside it.
* price comes **down** into a bullish gap and **up** into a bearish one. That is the "after a
  decline / after a rally" the cheat sheet reads the hammer shapes by, so a long lower wick is a
  hammer at a bullish gap and a hanging man at a bearish one (see below).
* if a bar completes both a bullish and a bearish pattern, the stronger one (table below)
  decides. When they are equally strong it is no reaction yet, and the gap is still watched.
* the candle that completes a gap never counts as its retest: it sits right on the gap's edge.

## Candlestick patterns

All the patterns of the cheat sheet are kept. Ten new ones are added (marked **new**): the
*confirmed* versions of the cheat sheet's two-candle patterns (a harami or an engulfing that
the next candle confirms), the doji star, the outside reversal bar (it suits futures, which
rarely gap, and FVG retests, where price stabs into the gap and snaps back) and the four-candle
three-line strike.

Strongest first. The order decides which pattern a label names first and which one wins when
a bar shows patterns both ways. It runs from the patterns that show a complete, confirmed turn
to the single candles, with the ones usually read as needing confirmation last.

| Bullish | Bearish | Candles | Rule |
|---|---|---|---|
| Bullish three-line strike **new** | Bearish three-line strike **new** | 4 | three bearish candles, each closing lower, then a bullish candle that opens at or below the last close and closes above the first one's open, wiping all three out |
| Morning doji star **new** | Evening doji star **new** | 3 | a morning (evening) star whose star is a doji |
| Morning star | Evening star | 3 | a long bearish candle, a small star whose body stays below the middle of the first body, then a long bullish candle closing above that middle |
| Three outside up **new** | Three outside down **new** | 3 | a bullish engulfing, then a candle closing higher still |
| Three white soldiers | Three black crows | 3 | three long bullish candles, each opening inside the body before it, closing higher and in the top quarter of its range |
| Bullish outside reversal **new** | Bearish outside reversal **new** | 2 | trades below the previous low and above the previous high, and closes above the previous high: a bullish candle (bearish: closes below the previous low) |
| Bullish engulfing | Bearish engulfing | 2 | a bearish candle, then a bullish one with a bigger body, at least an average one, covering its whole body |
| Three inside up **new** | Three inside down **new** | 3 | a long bearish candle, a small candle whose body stays inside it, then a bullish candle closing above the first one's open |
| Piercing line | Dark cloud cover | 2 | after a long bearish candle, a bullish one that opens at or below its close and closes above the middle of its body, but below its open |
| Hammer | Shooting star | 1 | a lower wick at least 2× the body (*Hammer wick / body*), the upper wick at most a tenth of the range |
| Dragonfly doji | Gravestone doji | 1 | the same, when the body is at most a tenth of the range |
| Tweezer bottom | Tweezer top | 2 | a bearish then a bullish candle with the same low, within *Tweezer match* ticks (tops: bullish then bearish, same high) |
| Bullish marubozu | Bearish marubozu | 1 | a long candle with (almost) no wicks, at most 5% of its range each |
| Bullish harami | Bearish harami | 2 | a long bearish candle, then a small bullish one whose body stays inside it |
| Inverted hammer | Hanging man | 1 | a shooting star's shape after a decline (inverted hammer), a hammer's shape after a rally (hanging man) |

The bearish column is the mirror image of the bullish one. Some sources name the three-line
strikes the other way round, after the trend that comes before them. Here every pattern is named after
the way it points.

* **Long and small bodies** are measured against the average body of the 14 candles before the
  pattern (*Average body*).
* **Context.** The hammer shapes (hammer, shooting star, dragonfly / gravestone doji, inverted
  hammer, hanging man) mean different things depending on where they appear. After a decline
  a long lower wick is a hammer and a long upper wick an inverted hammer, both bullish. After a
  rally they are a hanging man and a shooting star, both bearish. "After a decline" means at a
  bullish gap, at filled bids, or at a swept low. "After a rally" means at a bearish gap, at
  filled offers, or at a swept high. All other patterns point the same way wherever they appear.
* **Futures rarely gap between bars**, so "opens below the previous close" is read as "at or
  below", and a star needs no gap.
* A plain **doji** or **spinning top** is indecision and points neither way, so it doesn't count
  on its own; it shows up as the star of a morning / evening star.
* Each pattern pair has its own switch under *Candlestick Patterns*.

## Big fills and the reaction after them

For every closed bar the indicator reads the footprint: the price where **the most contracts
traded** counts as a big fill when it has at least *Min filled volume at one price* contracts
(150) and at least *Filled volume vs bar average* (3×) the bar's average volume per price.

Whoever waited there got filled:

* more volume traded on the **bid** (sellers hit it) → **resting bids were filled**. Price came
  down into them;
* more traded on the **ask** (buyers lifted it) → **resting offers were filled**. Price rallied
  into them.

The **reaction** is the first candlestick pattern, on the fill bar or one of the next
*Reaction window* bars (3), that closes away from the fill price. A bullish pattern closing above
it is a bullish reaction and a bearish pattern closing below it is a bearish one. The hammer
shapes are read by the context above. With no pattern in the window the fill is marked "no clear
reaction" (gray). Hover a bubble to see who was filled, the reaction and how far price went
up and down in the window.

## Resting orders (Level 2)

While the chart is open the indicator follows the order book and the time & sales that ATAS
streams to it. Every price where at least **70 contracts** (*Min resting order*) sit on the bid or
the offer is a resting order. It is drawn as a band on its price from the bar where it
appeared, with its size at the right edge. **These are the big orders that have not been filled
yet.** The panel counts them and names the largest.

When such a level drops below 70, the indicator checks the trades printed at its price:

* **Filled**: trades against it (sells into a bid, buys into an offer) took at least *Filled when
  traded* (50%) of its size. The band stops there. If the bar's big fill is at that price, on that
  side, its bubble gets a white ring. If not, the order becomes a big fill of its own (a bubble
  with a ring) once at least *Min filled volume* (150) traded against it. Its reaction is read like
  any other fill.
* **Pulled**: the trades at its price did not reach that share within 2 seconds, so the orders
  were cancelled rather than filled.
* **Left the visible depth**: not filled either, but it was the deepest level in the book when it
  vanished, so it has most likely just scrolled out of the depth the feed sends.

Pulled orders and the ones that left the visible depth are hidden unless *Show pulled orders*
is on.

What Level 2 can and can't do inside an indicator:

* ATAS gives indicators the order book **live**, not the history behind its heatmap. Resting
  orders and order-book fills start from the current book when the chart loads, and build up
  while it stays open. They reset whenever the chart recalculates, for example after a
  settings change.
* The book shows **contracts per price**, not individual orders, so "70 limit orders" is read
  as 70 contracts resting at one price. Set *Min resting order* to 71 for strictly more than 70.
* It only covers the depth your feed sends, often 10 prices each side for CME futures. A big
  order further away is seen once price comes close, and one that price moves away from drops
  out of view.
* Because history has no order book to replay, **signals never use Level 2**. They use candles
  and the footprint, which history has too, so they come out the same after a reload.

## Liquidity sweeps

A sweep is a bar that wicks beyond the high / low of the last *Swing Lookback* bars (10) and closes
back inside: a stop run. A dotted line joins the swing it took to the sweep bar.

## BUY / SHORT signals

A signal is decided when a bar **closes**, so it never repaints. It needs a trigger (*Signal
source*):

* **FVG**: a reaction off a gap. A bullish reaction gives a BUY, a bearish one a SHORT.
* **Sweep+FVG**: an FVG reaction within *Sweep -> FVG window* bars (10) after a sweep on the same
  side: liquidity is taken, then price reverses out of an imbalance.
* **Fill**: a reaction to a big footprint fill.
* **Sweep**: a liquidity sweep: of lows for a BUY, of highs for a SHORT.

When a bar has several, the FVG reaction comes first, then the fill reaction, then the sweep.

Each signal counts up to four confirmations, and any of them can be made mandatory:

* **trend**: the close is above (buys) or below (shorts) the 50 EMA;
* **delta**: the bar's delta points the signal's way;
* **order flow**: a big fill of resting bids in the lower 35% of the signal bar or the bar before
  (buys), or of resting offers in the upper 35% (shorts). The passive side absorbed the
  aggression and price didn't go through;
* **candlestick pattern**: FVG and fill reactions always have one; a sweep counts it when the
  sweep bar completes a pattern pointing its way.

Entry is the signal bar's close; TP and SL are *Take profit* / *Stop loss* ticks away. With the
default 80 / 80 on NQ that is 20 points each way ($400 per NQ contract, $40 per MNQ). Once the
trade is *Break-even trigger* ticks in profit (40), its stop moves to *Break-even stop* ticks in
profit (+20), so from then on it ends at +80 or +20. Set the trigger to 0 to trade the plain
80 / 80 bracket.

### Where the probability comes from

Every signal on the chart is followed forward until it ends: at the TP, at the break-even stop
or at the SL. When a new signal fires, its odds are the shares of **earlier, already finished**
signals of the same kind that ended each way. So the numbers on an old signal are exactly what
the indicator would have shown live (no look-ahead).

"The same kind" is layered: same direction → same trigger → same number of confirmations. A
narrow group with only a few past trades is blended with its broader parent group
(`p = (count + k·p_parent) / (trades + k)` for each ending, *k* = *Probability smoothing*). The
starting point is the odds of a coin-flip market. For 80 / 80 with the stop moving to +20 at +40
that is **TP 22%, BE 45%, SL 33%**, worth 0 ticks on average (without break-even: 50 / 50). A
fresh chart shows those numbers everywhere, and they only move as real outcomes accumulate. Load
more history for more reliable estimates.

**EV** (expected value) combines the three: `TP% × 80 + BE% × 20 − SL% × 80` ticks. A positive EV
means that kind of signal has paid on this chart so far.

### Reading the chart

```
BUY  FVG · Hammer                  [OPEN]   <- side, trigger, candlestick pattern ("+1" = one more), badge
TP 31%  BE 41%  SL 28%   EV +6t             <- odds of each ending (they add up to 100), expected ticks
```

Once the trade ends, the card shrinks to a chip with its result: **TP +80t** (green), **BE +20t**
(amber, stopped at +20), **SL -80t** (red) or **EXP** (expired). Switch *Compact labels for closed
trades* off to keep every card. Hover a card or a chip for the full breakdown:

* the prices and the break-even levels;
* the TP / BE / SL counts behind the estimate, at each level of grouping;
* which confirmations were present, and every pattern the bar completed;
* the gap or the fill it reacted to;
* when the stop moved and how the trade ended.

By default every signal is shown, weak ones included. A card with a negative EV (hover a chip to
see an old one's) tells you that setup has cost ticks so far. Once the panel's track record shows the labels holding up on your
chart, set **Min expected ticks to show** (e.g. 5) to keep only the better setups on screen.
Hidden signals are still followed, so the statistics keep learning from them.

The panel shows:

* TP / BE / SL counts and net ticks for longs, shorts and total (signals shown on the chart);
* open, expired and hidden signals, and how many settled trades the model has learned from;
* **track record**: the average ticks actually made by signals labelled with a positive EV and
  by the rest, so you can see whether the labels have been reliable on this chart;
* the big fills so far and how many had a bullish or bearish reaction, and the resting orders
  of 70+ in the book now, with the largest;
* for an open trade, **live odds** from the current price, e.g.
  `Live BUY +44t, stop +20t:  TP 38% · BE 62% · SL 0%`. Each leg (reaching +40 before the stop,
  then TP before the break-even stop) is a random walk with the drift implied by the entry odds.

## Tuning

* **Too many bubbles?** *Min filled volume at one price* is a plain contract count, so the right
  value depends on the instrument, the timeframe and the session. Raise it, or the multiplier,
  until only the fills that stand out are left.
* **Resting orders.** 70 contracts at one price usually stands out on NQ. On markets with a much deeper
  book, such as ES, most levels hold more than that, so raise *Min resting order* there.
* **Gaps piling up in a trend?** Lower *Zone Max Age* or raise *Min FVG Size*. For fewer, cleaner
  reactions switch off the weaker patterns (marubozu, harami, inverted hammer / hanging man),
  or turn on *Require candlestick pattern* to filter the sweeps.

## Honest limits

* The probability is a frequency from the history loaded on the chart, not a guarantee. Markets
  change; small samples stay close to the coin-flip odds by design.
* Fills are idealised: entry at the close, exact TP / SL, no slippage or commission.
* On historical bars the order of the high and low inside one bar is unknown, and it matters
  whenever a bar holds both the TP and the SL, or reaches +40 and also trades back to +20.
  * By default the indicator assumes price went from the open to the **nearer extreme first**,
    the assumption TradingView's strategy tester makes.
  * *Worst case* assumes the order that hurts the trade. With break-even on, that means any bar
    that reaches +40 from an open below +20 counts as stopped at break-even, which is very
    pessimistic on 1-minute charts.
  * Live bars follow the trades as they happen, so reloading the chart can occasionally settle
    such a trade differently.
* Candlestick patterns are rules of thumb with fixed thresholds (wicks at most a tenth of the
  range, bodies against a 14-bar average...). They describe what the candles did, not what they
  will do.
* The order book is live only and aggregated per price. Iceberg orders that refill, or orders
  pulled a moment before price arrives, look like any other resting or pulled order.
* Signals close together often ride the same move. The cooldown limits that, but *n* can still
  overstate the independent evidence.

## Install

### Mac (ATAS X)

On a Mac, ATAS runs as **ATAS X**. You build the indicator once with Microsoft's free .NET
tools, then copy one file into ATAS X.

1. Install **ATAS X** for macOS if you haven't already, in the usual Applications folder.
2. Install the **.NET 10 SDK** from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0)
   using the macOS installer: **Arm64** for Apple Silicon (M1–M4), **x64** for an Intel Mac.
   (Apple menu → About This Mac shows which chip you have.)
3. On this repository's GitHub page, click **Code → Download ZIP** and double-click the ZIP to unzip it.
4. Open **Terminal**, type `cd ` (with a space), drag the unzipped folder onto the Terminal
   window, press Return, then run:

   ```
   dotnet build FvgReactionLiquiditySweep.csproj -c Release
   ```

   It finds the ATAS X libraries in `/Applications/ATAS X.app/Contents/MonoBundle`. If ATAS X is
   somewhere else, add `-p:ATAS_BASE="/path/to/ATAS X.app/Contents/MonoBundle"`.
5. Copy the result into ATAS X's indicator folder:

   ```
   mkdir -p ~/Library/Application\ Support/ATAS/Indicators
   cp bin/Release/net10.0/FvgReactionLiquiditySweep.dll ~/Library/Application\ Support/ATAS/Indicators/
   ```

6. ATAS X loads new or updated indicator files automatically. Add **FVG Reaction + Liquidity
   Sweep** to a chart from the *My Indicators* group.

### Windows (classic ATAS Platform)

1. Build the DLL (needs ATAS installed and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)):

   ```
   dotnet build FvgReactionLiquiditySweep.csproj -c Release
   ```

   If ATAS is not in `C:\Program Files (x86)\ATAS Platform`, add `-p:ATAS_BASE="D:\path\to\ATAS Platform"`.
   Or drop `FvgReactionLiquiditySweep.cs` into an indicator project you already have.
2. Copy `bin\Release\net10.0-windows\FvgReactionLiquiditySweep.dll` into `%APPDATA%\ATAS\Indicators`
   (or `Documents\ATAS\Indicators`). ATAS builds that still run on .NET 8 need the DLL from
   `bin\Release\net8.0-windows` instead.
3. Restart ATAS and add **FVG Reaction + Liquidity Sweep** from the *My Indicators* group.

According to the ATAS docs, this Windows DLL also loads in ATAS X, since the indicator has no
custom WPF editors. On Windows you can also build a native ATAS X DLL with `-p:AtasX=true`.

**Data.** The big fills and the delta confirmation read footprint data, so they need a feed with
tick data (e.g. Rithmic). The resting orders need the feed's market depth (Level 2), and only
show while the chart is open.

## Settings

| Group | Setting | Default | |
|---|---|---|---|
| Liquidity Sweep | Swing Lookback (bars) | 10 | bars whose high / low a sweep must run |
| | Show sweeps | on | the dotted sweep lines |
| Fair Value Gap | Min FVG Size (ticks) | 4 | |
| | Zone Max Age (bars) | 150 | a gap nothing used or filled within this many bars stops being watched |
| | Require reaction close beyond zone | on | a bullish reaction must close above the gap, a bearish one below it |
| | FVG is filled when | Price reaches its far edge | or: a candle closes beyond it; price reaches its middle (50%) |
| | Draw FVG Zones, Show zone midline (50%) | on | |
| | Show used / filled zones | off | a faint box for each gap that is no longer watched |
| | Bullish zone color, Bearish zone color | translucent teal / red | |
| Order Flow | Show big fills | on | the bubbles |
| | Min filled volume at one price | 150 | contracts at the bar's busiest price |
| | Filled volume vs bar average (x) | 3.0 | and this many times the bar's average per price |
| | Reaction window (bars) | 3 | bars after the fill bar a reaction may take |
| | Show resting orders | on | live, from Level 2 |
| | Min resting order (contracts) | 70 | at one price |
| | Filled when traded (%) | 50 | share of an order that must trade at its price for it to count as filled rather than pulled |
| | Show pulled orders | off | a faint trace of big orders that were cancelled |
| Signals | Buy signals, Short signals | on | |
| | Signal source | FVG reaction, fill reaction or sweep | or FVG reaction only, liquidity sweep only, sweep then FVG reaction, fill reaction only |
| | Sweep -> FVG window (bars) | 10 | |
| | Trend EMA period (0 = off) | 50 | |
| | Only trade with the trend / Require delta / Require order-flow confirmation / Require candlestick pattern | off | |
| | Cooldown between signals (bars) | 3 | per direction |
| | One trade at a time | on | new signals while a trade is open are hidden but still learned from |
| | Min TP probability to show (%) | 0 | hide signals with lower TP odds; they are still learned from |
| | Min expected ticks to show (0 = off) | 0 | hide signals with a lower EV; they are still learned from |
| Candlestick Patterns | Hammer / Shooting star, Inverted hammer / Hanging man, Engulfing, Piercing line / Dark cloud cover, Harami, Tweezer bottom / top, Morning star / Evening star, Three white soldiers / black crows, Marubozu, Three inside up / down, Three outside up / down, Outside reversal, Three-line strike | on | one switch per pattern pair; the doji stars go with the stars, the dragonfly / gravestone dojis with the hammers |
| | Hammer wick / body (min) | 2 | for hammers, shooting stars, inverted hammers and hanging men |
| | Average body (bars) | 14 | the yardstick for long and small bodies |
| | Tweezer match (ticks) | 1 | how far apart a tweezer's two lows (highs) may be |
| Take Profit / Stop Loss | Take profit (ticks) / Stop loss (ticks) | 80 / 80 | |
| | Break-even trigger (ticks, 0 = off) | 40 | profit at which the stop moves |
| | Break-even stop (ticks in profit) | 20 | where it moves to (0 = the entry price); kept below the trigger |
| | Max bars in trade (0 = no limit) | 0 | expired trades are left out of the probabilities |
| | Close trades at session end | off | also takes no new signal on a session's last bar |
| | Order of high and low inside a bar | Open to the nearer extreme first | or worst case for the trade, or candle direction (O-L-H-C / O-H-L-C) |
| | Probability smoothing (virtual trades) | 10 | higher = steadier numbers that need more history to move |
| Display | Show signal labels, Compact labels for closed trades, Show TP / SL levels, Show statistics panel | on | closed trades keep a small result chip and a faint box |
| | Show reaction labels | off | name the pattern next to each reaction marker (hovering one always does) |
| | Statistics panel position, Label offset (px), Label font, Take profit / Stop loss / Break-even line | top right, 24, Arial 9 | |
| Alerts | Alert on new signal / Alert on TP / SL / break-even / Alert sound file | off / off / alert1 | the second also fires when a stop moves to break-even; only in real time, never while history loads |

The signal arrows are regular data series (*Buy Signal*, *Short Signal*), so ATAS can also use
them for alerts or automation. The reaction and sweep series (*FVG Bull Reaction*, *Liquidity
Sweep Low*...) keep their names and values for the same purpose, but are hidden, since the chart
draws its own markers for them.

## Changes in this version

This version is a rewrite.

* **Kept**: the Fair Value Gaps and their settings, liquidity sweeps, every pattern of the cheat
  sheet, and the BUY / SHORT signals with their TP / BE / SL odds (80 / 80, break-even +40 → +20).
  The walk-forward statistics, the panel, the tooltips and the alerts are kept too.
* **New**:
  * gaps stop being watched once used, filled or too old;
  * reactions are read from candlestick patterns, bullish or bearish, at either kind of gap;
  * ten new patterns;
  * big fills and the reactions to them (a new *Fill* trigger, and the order-flow confirmation);
  * resting orders of 70+ from Level 2;
  * a cleaner look: a new color scheme, cards only for the open trade (closed ones shrink to a
    result chip), used gaps taken off the chart, and pattern names on hover.
* **Removed**:
  * the absorption heatmap and its confirmation, replaced by the big fills and the resting orders;
  * the old reaction rule (a close back out of the gap with a candle in its direction);
  * the *Use candlestick patterns* switch, since reactions are now made of patterns. Switch
    single patterns off instead.

The fixes made to the original file are all still in:

* it compiles against the current ATAS API on classic ATAS and ATAS X;
* nothing is decided on the forming bar, so nothing repaints;
* the candle that completes a gap is not its own retest;
* rendering is thread-safe;
* only the visible bars are drawn;
* it resets through `OnRecalculate`.

## Tests

`tests/` builds the indicator against stand-ins for the ATAS API and checks it on scripted and
randomised markets, including a simulated order book — see [tests/README.md](tests/README.md).
They are not part of the indicator build.
