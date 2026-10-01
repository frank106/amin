# FVG Reaction + Liquidity Sweep — ATAS indicator

A custom indicator for the [ATAS](https://atas.net) platform that:

1. **reads candlestick patterns**: the ones on [TraderLion's cheat sheet](https://traderlion.com/technical-analysis/candlestick-patterns-cheat-sheet/)
   plus ten stronger, confirmed ones;
2. uses those patterns to tell whether price reacted **bullish or bearish off a Fair Value Gap**;
3. finds **the price inside each bar where the most contracts were filled** (big compared with
   recent bars, so the size follows the market), marks whether the resting bids or offers were
   filled there, and reads the reaction that followed (bullish or bearish);
4. shows the **resting limit orders of 70+ contracts still waiting in the order book**, live
   from Level 2, and tells filled orders from pulled ones;
5. **stops watching a gap once it has been used or filled**;
6. marks the **key levels** where stops pile up: the prior day's, the overnight and the opening
   range's high and low, and equal highs / lows. It shows which ones price swept and which it broke;
7. turns reactions and liquidity sweeps into **BUY / SHORT signals with the odds of each way the
   trade can end**: the take profit (80 ticks), the break-even stop (the stop moves to +20 ticks
   once the trade is 40 ticks in profit) or the stop loss (80 ticks). By default they only fire
   during regular hours, 09:30–16:00 New York time. The bracket can instead follow the market (a
   multiple of the ATR, or the stop just beyond the signal bar), and the entry can be a limit order
   on a pullback;
8. keeps a **scoreboard** of how each setup and each candlestick pattern has done on your chart,
   and checks whether the odds it showed held up;
9. can **send the orders of its own signals**, off by default: paper (simulated) unless you arm it
   for a live account, one position at a time and one live trade per account, with a daily loss
   limit, an optional flat-by time and a log of every order (see [Execution](#execution-sending-the-orders)).

![Layout preview](docs/preview.png)

*Layout preview produced by the test harness from synthetic data (a 1-minute chart in regular
hours), with a simulated order book and the mouse over the overnight low's tag. It is not an ATAS
screenshot: ATAS draws the candles and arrows itself, with its own fonts and theme.*

## What it draws

| On the chart | Meaning |
|---|---|
| Teal / red boxes reaching the right edge | Bullish / bearish Fair Value Gaps still being watched, with a dashed line at their middle (50%) |
| Small ▲ / ▼ | A bullish / bearish reaction off a gap that did not become a signal on the chart. Hover it for the pattern (or switch on *Show reaction labels* to name it, e.g. `Hammer`) |
| Bubbles | Big fills: the price inside a bar where the most contracts traded (live, also a big resting order the order book saw filled). Bigger = more contracts. **Green** = the reaction after it was bullish, **red** = bearish, **gray** = no clear reaction; faint while it is still being watched |
| White ring around a bubble | The order book saw a resting order of 70+ contracts filled right there |
| Blue / orange bands with a size tag | Resting **bids / offers of 70+ contracts** still waiting in the order book, from the bar where they appeared; the tag at the right edge shows their size. The bigger the order, the stronger the color. A filled one stops, faintly, where it was filled |
| Dotted line ending in a dot | A liquidity sweep: from the swing high / low that held the stops to the bar that ran them |
| Lavender lines labelled `PDH`, `ONL`, `ORH`, `EQL`... | Key levels: prior day, overnight and opening range highs / lows, equal highs / lows. Bright while untouched. Where a bar swept one, the line stops, dimmer, with a dot (red for highs, green for lows). Faint and dashed where a bar closed beyond it |
| **Large arrows + card** | **BUY / SHORT signal** of the open trade: the setup, the odds of TP / break-even / SL and the expected ticks |
| Large arrows + small chip | A signal whose trade has ended, with its result: `TP +80t`, `BE +20t`, `SL -80t` or `EXP` |
| Shaded boxes | The open trade: entry → TP shaded green, entry → SL shaded red, amber dotted line at the break-even trigger (+40 ticks), solid amber once the stop has moved to +20, price tags at the right end. Trades that have ended leave a faint box |
| Panel | Results, the labels' track record, the signal hours, the fills and resting orders so far with the size each needs now, and the live odds of the open trade; with execution on, the executor's mode, day and position. Hover it for the scoreboard |

Hover the mouse over a card or chip, a bubble, a reaction marker, an order band or a key level's
label for the details.
Gaps that are no longer watched can also be kept as faint boxes (*Show used / filled zones*).

## Fair Value Gaps

A gap is the classic three-candle imbalance: a **bullish** gap when the third candle's low is
above the first candle's high, a **bearish** one when the third candle's high is below the first
candle's low, by at least *Min FVG Size* ticks (4). *Min FVG size (× ATR)* can also ask for a
share of the average true range of the bars before the gap, so small gaps in a busy market don't
count (off by default).

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

For every closed bar the indicator reads the footprint. The price where **the most contracts
traded** is a big fill when it stands out twice:

* **against recent bars** (*Big fill size*). By default it has to rank in the top *Top share of
  recent bars* (10%) of the busiest prices of the last *Fill lookback* bars (200). So the size
  follows the instrument, the timeframe and the time of day on its own. It is measured against
  earlier bars only (no look-ahead), and needs 20 bars with a footprint to start. The panel shows
  the size it takes now (`big ≥412`). Set *Big fill size* to *A fixed number of contracts* to use
  *Min filled volume at one price* (150) instead;
* **inside its bar**: at least *Filled volume vs bar average* (3×) the bar's average volume per
  price.

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

*Resting order size* can instead ask for a multiple of what is normal in the book: at least
*Resting order vs typical level* (5×) the median size of the prices in the book when the order
appears (70 while the book shows fewer than 5 prices). Each order keeps the size it was measured
against, so it doesn't come and go as the rest of the book changes.

When such a level drops below its size, the indicator checks the trades printed at its price:

* **Filled**: trades against it (sells into a bid, buys into an offer) took at least *Filled when
  traded* (50%) of its size. The band stops there. If the bar's big fill is at that price, on that
  side, its bubble gets a white ring. If not, the order becomes a big fill of its own (a bubble
  with a ring) once as many contracts traded against it as a big fill needs (the panel's `big ≥`,
  or *Min filled volume*, 150, with a fixed size). Its reaction is read like any other fill.
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

Two filters keep only the clearer stop runs. Both are off by default:

* **Min sweep depth** (× ATR): the wick has to reach this many average true ranges beyond the
  high / low. A poke of a tick or two takes few stops. The ATR is the average range of the
  *Volatility (ATR) period* bars (20) before the sweep bar, so the depth follows the market. A
  key level taken by a shallower wick still ends as swept, but gives no signal.
* **Sweep confirmed swings only**: the bar has to take out a swing high / low of the lookback
  (higher / lower than the 3 bars on each side) that nothing has traded beyond since. When it
  takes several, the line goes to the furthest one. A swing counts once: after a bar has traded
  beyond it, it is gone. Without this filter, any bar that wicks beyond the lookback's high / low
  counts, even when that was the last bar's.

## Key levels

Some highs and lows are watched by everyone, so stops pile up just beyond them. The indicator
marks them in New York time, whatever time zone the chart shows (summer time included). Its
trading day starts at 18:00, when CME's overnight session opens.

| Level | Tag | The high and low of | Watched from |
|---|---|---|---|
| Prior day | `PDH` / `PDL` | the previous regular session (*Regular hours start / end*: 09:30–16:00) | the start of the trading day |
| Overnight | `ONH` / `ONL` | 18:00 until regular hours open | the first bar of regular hours |
| Opening range | `ORH` / `ORL` | the first *Opening range* minutes (30) of regular hours | the first bar after the opening range |
| Equal highs / lows | `EQH` / `EQL` | two swing highs (lows) within *Equal level match* ticks (2) of each other, at most *Equal level lookback* bars (120) apart, with no bar trading beyond them in between | the bar after the second swing is confirmed |

A swing high is higher than the 3 bars before it, and none of the 3 after it goes higher, so it
is confirmed 3 bars later (swing lows the other way round). Equal highs are marked at the higher
of the two highs, equal lows at the lower of the two lows.

The first bar that trades beyond a level takes it, and the level is not watched after that:

* **swept**: the bar closes back inside. The stops were run and price came back. The line stops
  there with a dot;
* **broken**: the bar closes beyond it. Price accepted the other side. The line turns faint and
  dashed.

A day's levels that nothing took by the end of the trading day stop there. Equal highs / lows
stop once *Equal level lookback* bars have passed since their second swing. Hover a level's tag to
see what it is, when it started to count and how it ended.

## BUY / SHORT signals

A signal is decided when a bar **closes**, so it never repaints. It needs a trigger (*Signal
source*):

* **FVG**: a reaction off a gap. A bullish reaction gives a BUY, a bearish one a SHORT.
* **Sweep+FVG**: an FVG reaction within *Sweep -> FVG window* bars (10) after a sweep on the same
  side: liquidity is taken, then price reverses out of an imbalance.
* **Key sweep+FVG** (e.g. `PDL sweep+FVG`): the same after a sweep of a key level.
* **Fill**: a reaction to a big footprint fill.
* **Key sweep** (e.g. `Sweep ONH`): a bar that sweeps a key level: lows for a BUY, highs for a
  SHORT. When it sweeps several, the most important one names it: the prior day first, then the
  overnight range, the opening range and equal highs / lows. Between two of a kind, the further
  one wins: it took more stops.
* **Sweep**: a liquidity sweep: of lows for a BUY, of highs for a SHORT.

When a bar has several, the FVG reaction comes first, then the fill reaction, the key sweep and
the plain sweep. A key sweep outranks a plain one, so an FVG reaction after both is a *Key
sweep+FVG*.

Each signal counts up to four confirmations, and any of them can be made mandatory:

* **trend**: the close is above (buys) or below (shorts) the 50 EMA;
* **delta**: the bar's delta points the signal's way;
* **order flow**: a big fill of resting bids in the lower 35% of the signal bar or the bar before
  (buys), or of resting offers in the upper 35% (shorts). The passive side absorbed the
  aggression and price didn't go through;
* **candlestick pattern**: FVG and fill reactions always have one; a sweep counts it when the
  sweep bar completes a pattern pointing its way.

### Entry, take profit and stop loss

By default the entry is the signal bar's close, and TP and SL are *Take profit* / *Stop loss*
ticks away. With the default 80 / 80 on NQ that is 20 points each way ($400 per NQ contract, $40
per MNQ). Once the trade is *Break-even trigger* ticks in profit (40), its stop moves to
*Break-even stop* ticks in profit (+20), so from then on it ends at +80 or +20. Set the trigger
to 0 to trade the plain 80 / 80 bracket.

*Bracket size* can make the bracket follow the market instead:

* **Multiples of the ATR**: the stop is *Stop loss (× ATR)* (3) average true ranges of the
  *Volatility (ATR) period* bars (20) before the signal, and the take profit *Take profit (× ATR)*
  (3). A busy market gets a wider bracket, a quiet one a tighter one.
* **Stop beyond the signal bar**: the stop sits *Stop buffer* ticks (2) beyond the signal bar's low
  (buys) or high (shorts), where the setup is proven wrong, and the take profit is *Take profit
  (× the stop)* (1.5) times as far.

Either way the stop is kept between *Min stop* (16) and *Max stop* (200) ticks, and the take
profit keeps its ratio to it. The break-even step is then a share of the take profit: the stop
moves once the trade is *Break-even trigger (% of TP)* (50%) of the way there, to *Break-even stop
(% of TP)* (25%) in profit. Each card, chip and tooltip shows the trade's own distances.

*Entry* can also be a **limit order on a pullback**: *Pullback* (25%) of the signal bar's range
back from its close (below it for a buy, above it for a short). The order waits *Limit order
valid* bars (3). It fills once price trades a tick through it, at the limit price, and the
bracket then counts from there. A dashed line with an `LMT` price tag marks it while it waits.
An order that doesn't fill is cancelled: its chip says *No fill*, the panel counts it, and it is
left out of the results and the odds. A waiting order counts as the open trade for *One trade at
a time*.

### Signal hours

By default signals only fire during **regular hours**, 09:30–16:00 New York time (*Signal
hours*). A bar counts by the time it opens, in New York time whatever time zone the chart shows.
Gaps, fills, sweeps and key levels are still found around the clock; only the signals wait. Bars
outside the hours give no signals at all, so they don't count in the statistics either. The
other choices:

* **All hours**: signals around the clock, as before;
* **First two hours of regular hours**: 09:30–11:30, usually the busiest stretch;
* **Regular hours, not 11:30 – 13:30**: skips the lunch lull;
* **Custom window**: from *Custom window start* to *Custom window end*, e.g. 09:30–10:30. A
  window that ends before it starts runs over midnight; the same start and end means all day.

The panel shows which one is on, with the New York time of the last bar to check it against.

### The day's trend

With *Only trade with the day's trend* on (it is off by default), signals only go the day's way:

* **up**, BUYs only: the signal bar closes above both the day's open and its VWAP;
* **down**, SHORTs only: it closes below both;
* **no clear trend**, no signal: it closes between the two.

The day is the regular session from its first bar (09:30 New York), with the open of that bar and
the VWAP - the volume-weighted average of the bars' typical price, (high + low + close) / 3 - since.
Before the session opens, the day counts from 18:00, when the trading day starts. A signal against
the day's trend is not taken at all, as with the other filters, so the chart, its statistics, the
backtest and the executor all see the same signals. The panel shows the day's trend, its open and its
VWAP.

It was on by default in the version that brought it, and is off since its first backtest: on 50
days of MNQ, trades averaged roughly 4–5 ticks less with it than without it, on the bars as assumed
and in the worst case alike (see [On ATAS's own data](#on-atass-own-data)).

### Premium and discount

With *Buy in discount, short in premium* on (off by default), a BUY needs its signal bar to close
below the middle of the range between the last swing high and swing low - the discount half - and
a SHORT above it, in the premium half. A swing high is a bar with no higher high within *Premium /
discount swing length* bars (50) on either side, a swing low likewise, and a run of swings of one
kind keeps its most extreme one. The idea and these definitions come from
[joshyattridge/smart-money-concepts](https://github.com/joshyattridge/smart-money-concepts) (MIT),
whose swings look at the bars after a swing as soon as it forms; here a swing only counts once
those bars have closed, so history reads as it did live and a backtest can't peek ahead. The panel
says which half the last close is in, and the range.

On 50 days of MNQ settled on the real 1-second path, it was the one idea from that package that
helped: FVG reactions (*Signal source: FVG reaction only*) went from losing to about break-even a
trade, and with the executor's daily rules they came out ahead in both halves of the test. On 15
months it still cut the losses on the months it was never tuned on (−$845 against −$2,423 without
it), but it didn't make them profitable. See [On ATAS's own data](#on-atass-own-data).

### ADX and yesterday's value area

Two more filters, off by default, that both cut the losses on months the setup was never tuned on,
without making them profitable:

* *Max ADX* (0 = off): signals only while Wilder's 14-bar ADX is below it - a quiet, ranging
  market. At 20 it improved each trade in both unseen stretches of the 15-month test (from −4.7 to
  −3.0 ticks and from −3.5 to −0.4); 15 and 25 did not, so the level matters and may be partly luck.
* *Only outside yesterday's value area*: BUYs only below the value area low of the last regular
  session to finish, SHORTs only above its value area high. The value area is the prices around
  the session's busiest one that held 70% of its volume, from the footprint (a bar without one
  counts its volume spread evenly over its range). It cut the losses mostly by trading less; on
  top of premium and discount it hardly changed each trade, and with Max ADX 20 it did worse.

The panel shows the ADX and yesterday's value area while either is on.

### Quiet days and news days

With *Skip the day after a quiet one* set (a share of the usual range, 0 = off), there are no signals
on a day that follows a regular session whose range was under that share of the average of the 20
sessions before it. It needs 21 finished sessions on the chart, so load 30 days. At 70%, on top of
the setup in [On ATAS's own data](#on-atass-own-data) with *Max ADX* 20, it improved both unseen
stretches of the 15-month test - the executor's −$113 → +$15 and −$258 → −$48, each trade −3.0 →
−1.9 and −0.4 → +0.8 ticks - and 60% did too, but 80% and 90% did not, so the level matters and may
be partly luck. The panel says how the last session's range compared.

Two related ideas didn't help, so they aren't built in:

* skipping FOMC statement days and CPI release days (from the Federal Reserve's and the BLS's
  calendars; 8 and 13 of the 252 days with signals) helped the design months and hurt the holdout.
  Signals from 09:30 to 12:30 come after CPI's 08:30 release and before FOMC's 14:00 statement;
* skipping days that turned out to trade in a narrow range - which a live bot can't know until the
  day is over - made each remaining trade worse even with that hindsight (−3.0 → −4.1 ticks in the
  design months): the losses aren't on the choppy days.

### Where the probability comes from

Every signal on the chart is followed forward until it ends: at the TP, at the break-even stop
or at the SL. When a new signal fires, its odds are the shares of **earlier, already finished**
signals of the same kind that ended each way. So the numbers on an old signal are exactly what
the indicator would have shown live (no look-ahead).

"The same kind" is layered: same direction → same trigger → then each split that is switched on,
nested in the one before:

* *Odds by time of day* (off): the first 30 minutes of regular hours, the rest of the morning
  (to 11:30 with the default hours), midday (to 14:00), the afternoon, or outside regular hours;
* *Odds by volatility* (off): quiet, normal or busy, from the ATR of the bars before the signal
  against one ten times as long (under 0.8×, over 1.25×);
* *Odds by trend* (off): with the trend EMA or against it;
* *Odds by confirmations* (on): the number of confirmations.

A narrow group with only a few past trades is blended with its broader parent group
(`p = (count + k·p_parent) / (trades + k)` for each ending, *k* = *Probability smoothing*), so
each split you add needs more history before it says anything. The tooltip lists every group
with its counts. The starting point is the odds of a coin-flip market for the trade's own
bracket. For 80 / 80 with the stop moving to +20 at +40 that is **TP 22%, BE 45%, SL 33%**, worth 0
ticks on average (without break-even: 50 / 50). A fresh chart shows those numbers everywhere, and
they only move as real outcomes accumulate. Load more history for more reliable estimates.

**EV** (expected value) combines the three: `TP% × 80 + BE% × 20 − SL% × 80` ticks, with each
trade's own distances. A positive EV means that kind of signal has paid on this chart so far.

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
* open, expired and hidden signals, limit orders that didn't fill (with a limit entry), and how
  many settled trades the model has learned from;
* the signal hours, and the New York time of the last bar;
* **track record**: the average ticks actually made by signals labelled with a positive EV and
  by the rest, so you can see whether the labels have been reliable on this chart;
* the big fills so far, how many had a bullish or bearish reaction and the size a big fill takes
  now; the resting orders in the book now, the size they need and the largest;
* for an open trade, **live odds** from the current price, e.g.
  `Live BUY +44t, stop +20t:  TP 38% · BE 62% · SL 0%`. Each leg (reaching +40 before the stop,
  then TP before the break-even stop) is a random walk with the drift implied by the entry odds.
  While a limit order waits, the line shows its price and the bar it waits until.

### Scoreboard

Hover the panel, or switch on *Keep the scoreboard open*, for a table of how each kind of signal
has done on this chart. It opens under the panel (above it when the panel sits at the bottom).
Every signal that ended at TP, BE or SL counts, hidden ones included; expired ones don't.

```
Scoreboard: 118 closed signals, hidden ones included
Setup          n   TP   BE   SL     avg
FVG           41    9   17   15   -3.4t
Key sweep     23    8    9    6  +14.8t
...
Pattern        n   TP   BE   SL     avg
Hammer        17    5    7    5   +8.2t
...
Odds check (were the TP odds right?)
Said TP 20–30%: hit 24% of 41
...
```

* **Setup**: a row per trigger (FVG, Sweep, Sweep+FVG, Fill, Key sweep, Key sweep+FVG).
* **Pattern**: a row per candlestick pattern (the ten most frequent) and one for *No pattern*. A
  signal whose bar completed several patterns counts in each of their rows.
* Each row: the number of signals, how many ended at TP / BE / SL, and the average ticks per
  signal, green when it made money, red when it lost.
* **Odds check**: the signals grouped by the TP odds their label showed (under 20%, 20–30%, ...,
  60% and more), with how often they really hit the TP. A line like `Said TP 30–40%: hit 35% of
  52` means the odds have been honest on this chart.

Use it to prune: switch off the patterns that keep losing ticks, pick a narrower *Signal source*,
or change the hours. A row with a handful of signals is mostly noise, so wait for a few dozen
before you judge it.

## Tuning

* **Too many bubbles?** Lower *Top share of recent bars* (say to 5%), or raise the multiplier.
  With *A fixed number of contracts*, *Min filled volume at one price* is a plain count whose right
  value depends on the instrument, the timeframe and the session: raise it until only the fills
  that stand out are left.
* **Resting orders.** 70 contracts at one price usually stands out on NQ. On markets with a much deeper
  book, such as ES, most levels hold more than that: raise *Min resting order* there, or set
  *Resting order size* to *Times the typical level in the book*.
* **Too many lines?** Switch off the key levels you don't trade. Equal highs / lows are the most
  frequent; a smaller *Equal level match* or *Equal level lookback* keeps fewer. With *Draw key
  levels* off their sweeps still give signals, without the lines.
* **Gaps piling up in a trend?** Lower *Zone Max Age* or raise *Min FVG Size*. For fewer, cleaner
  reactions switch off the weaker patterns (marubozu, harami, inverted hammer / hanging man),
  or turn on *Require candlestick pattern* to filter the sweeps.
* **Prune with the scoreboard** once its rows hold a few dozen signals each: switch off the
  patterns and setups that keep losing ticks on your chart.
* **Test a change before you trust it**: replay your own history through the backtest runner,
  pick settings on one stretch and judge them on a later one. With a break-even step, settle it
  on ticks (`--ticks`) or at least check the worst-case row: a result that exists only with the
  assumed order inside bars isn't there (see below).

## Backtest: two years of 1-minute NQ

The runner in `tests/Backtest` replays bars through the indicator (see
[tests/README.md](tests/README.md)). It was run on two years of 1-minute bars of Dukascopy's
Nasdaq-100 CFD (bid prices, 22 September 2024 – 24 September 2026, 519 trading days) as a stand-in
for NQ, and on the real ticks inside those bars for a random sample of 71 days, 36 in the first
year and 35 in the second (09:00–17:00 New York). It is the same index, but not the futures
contract, and it has no footprint, so there are no *Fill* signals and no delta or order-flow
confirmations. Costs are 1 tick of commission and 1 tick of slippage per market order: 3 ticks a
trade entered at the close.

**The defaults** (regular hours, 80 / 80, break-even +40 → +20), net a trade after costs:

| Settled | Year 1 | Year 2 |
|---|---|---|
| on the bars, as the indicator settles history | 0.0 ticks | +2.0 ticks |
| on the real ticks (the bars less what the ticks showed they add) | −2.1 ticks | −1.1 ticks |
| on the bars, worst-case order inside each bar | −8.7 ticks | −7.0 ticks |

Signal by signal, the bars made the default bracket 2.6 ticks a trade better than the ticks did
(2.1 in year 1, 3.2 in year 2). Settled on real ticks, the defaults lose about 1–2 ticks a trade
after costs. The "win rate" (TP or break-even, 67%) says little: random entries with this bracket
end at TP or break-even about as often.

**Where the bars go wrong.** Almost all of the difference comes from one kind of bar: the one that
reaches the break-even trigger while its range also covers the moved stop. How often the ticks
came back to the stop inside that same bar, with the default bracket:

| The bar | Stop hit in the same bar | On the bars |
|---|---|---|
| closes below the moved stop | 99% | always |
| closes between the stop and the trigger | 35% | never |
| closes beyond the trigger | 14% | never |
| also reaches the take profit | 18%, before the TP | never |

No fixed order of a bar's high and low gets this right. Each case is a mix, the rule counts the
likelier outcome, and so every miss goes the same way. Without break-even the bars settle 99.9%
of the trades as the ticks do. With the ATR-sized bracket, whose break-even step is about a bar
wide, they are about a tick a trade optimistic.

**The new settings, tested honestly.** Settings were chosen on the first year only (to 22
September 2025) and judged once on the second:

* On the bars, the best of about 400 combinations was signals only in the first two hours of
  regular hours, with a 40 / 40 bracket and break-even at +20 → +10. It made +3.0 ticks a trade
  after costs in year 1, and **+3.4 in year 2, with 13 of 13 months up**.
* On real ticks it loses: the bars overstate that bracket by 5–6 ticks a trade, more than all of
  its profit, so it comes out at −1.6 and −2.4. The second year couldn't catch this, because the
  error is in the bars and shows up in both years.
* Settled on real ticks, every bracket with a fixed break-even step lost in both years (eight of
  them, −0.5 to −2.7 ticks a trade), and so did the brackets without one (80 / 80: −4.8 and
  −2.2). The ATR-sized bracket (3 ATRs each way, with break-even) came out at +1.3 and −0.3: no
  edge, but the least bad, and better than the defaults in both years (by 3.4 and 0.8 ticks a
  trade).
* The plain move 5 to 60 minutes after a signal, which no bar order can bias, showed no reliable
  edge in year 1, in any hour window or with any filter. Neither the limit entries nor hiding
  signals with low expected ticks, with or without the new odds splits, turned the first year
  positive, even on the bars.

So the defaults are unchanged: nothing tested is profitable after costs on real ticks. If you
want the chart's odds to reflect what really happened, *Bracket size: Multiples of the ATR* is
the bracket that history on bars settles almost exactly. Judge any change on ticks (`--ticks`),
live, or at least next to the runner's worst-case row.

These runs came before *Only trade with the day's trend*, which is off by default, so the defaults
give the signals they had. On ATAS's own MNQ data below, it made them worse.

### On ATAS's own data

*FVG Bar Export*, the second indicator in the same DLL (also under *My Indicators*), writes the bars
a chart has loaded to files the runner reads - with what the CFD data above lacks: the futures'
own volume, each bar's delta, and its footprint (the volume, bid and ask at every price), so the
*Fill* signals and the delta and order-flow confirmations get backtested too. Load a chart with as
many days as the data feed gives and add the indicator. Once the history is in, it says on the
chart what it wrote to `%APPDATA%\ATAS\FvgExport` (or its *Folder*):

```
MNQZ6_1m_2026-06-01_2026-09-25_bars.csv            time,open,high,low,close,volume,delta,bid,ask
MNQZ6_1m_2026-06-01_2026-09-25_footprint.csv.gz    time,price,volume,bid,ask
```

Times are the bars' open times in UTC, and the forming bar is left out; with *Include the
footprint* off it writes the bars alone. Then, for MNQ ($0.50 a tick, so a $1.50 commission is 3
ticks):

```
dotnet run -c Release --project tests/Backtest -- <..._bars.csv> --footprint <..._footprint.csv.gz> --tick-value 0.5 --commission 3
```

Trades are still settled on an assumed path inside each bar; the worst-case row next to the result
shows how much that matters. A second export, from a **1-second chart** of the same instrument and
days (*Include the footprint* off), settles it: given with `--ticks`, the 1-second bars are read as
the path inside each minute - each second's open, the nearer of its high and low, the other, its
close - and trades follow it as on a live chart:

```
dotnet run -c Release --project tests/Backtest -- <..._1m_..._bars.csv> --footprint <..._1m_..._footprint.csv.gz> --ticks <..._1s_..._bars.csv> --tick-value 0.5 --commission 3
```

On a made-up market traded tick by tick, the 1-second bars settled every trade exactly as the
ticks themselves did. A 1-second chart of many days is heavy for ATAS; if it won't load them all,
export what it will: bars without seconds are settled on the bars, and the report says how much
of the trades' time the seconds covered.

**50 days of MNQ** (19 July – 25 September 2026, 1-minute bars with their footprint, the default
bracket, 5 ticks of costs a trade), net ticks a trade after costs:

| Settings | Signals | TP or BE | On the bars | Worst case |
|---|---|---|---|---|
| the defaults with the day's trend (then on by default) | 1,204 | 64% | −4.4 | −14.0 |
| the defaults without it | 2,691 | 67% | +0.2 | −10.3 |
| 09:30–12:00, overlapping trades, with the day's trend | 596 | 63% | −4.2 | −16.0 |
| 09:30–12:00, overlapping trades, without it | 1,544 | 67% | +0.9 | −10.4 |

The day's trend cost 4–5 ticks a trade in both pairs, which is why it is off by default now.
Nothing here is clearly profitable: the best average is 0.5 standard errors from zero, and on the
CFD's ticks above the bars overstated this bracket by 2.6 ticks a trade. The 1-second export
measures that on MNQ itself.

**On the 1-second path**, over the same 50 days, the defaults lost 4.8 ticks a trade after costs
instead of the +0.2 the bars showed: about 200 trades the bars counted as take profits had come
back to the break-even stop inside the minute. Every setting tested lost, the highest win rate
included (a 20-tick target with a 120-tick stop: 86% of trades made money, −3.9 ticks a trade).

**Ideas from smart-money-concepts**, tried on FVG reactions with a candlestick pattern, 09:30–12:30,
on the 1-second path, picked on July–August and checked on September:

| FVG reactions, 09:30–12:30 | Signals | Won money | Net a trade | Executor, 50 days | Jul–Aug | Sep |
|---|---|---|---|---|---|---|
| no filter | 617 | 67% | −4.1 | −$258 | −$85 | −$173 |
| with the market structure (last close beyond a 50-bar swing) | 317 | 65% | −7.5 | −$720 | −$630 | −$90 |
| in discount / premium (50-bar swings) | 260 | 72% | +3.0 | +$613 | +$135 | +$478 |

*Executor* is what it would have made on one MNQ contract: one position at a time, the +140-tick
daily target, and a $70 daily loss limit that skips trades that could breach it. Trading with the
market structure made things worse at every swing length from 5 to 50 bars, as the day's trend
did. Discount and premium made them better at 10 bars and up, in both halves and in regular hours
too (+$763), and made every other signal set tried a little less bad - but only the FVG reactions came out ahead,
and at under 1 standard error from zero that is not proof yet. The built-in filter gives the same
results as this test did.

**15 months on 10-second bars.** A 10-second export (19 June 2025 – 25 September 2026, 329 trading
days, footprint off) gives both the 1-minute bars, built from it, and the path inside them: on the
50 days above it settled trades about a tick a trade better than the 1-second path did (+$830
against +$613 for the setup below), a small, known optimism. The months before 19 July 2026 were
never used to tune anything:

| FVG reactions with a pattern, discount / premium, 09:30–12:30, executor | 15 months | Jun 2025 – Jul 18, 2026 (unseen) | Jul 19 – Sep 25, 2026 (tuned on) |
|---|---|---|---|
| as set up | −$15 | −$845 | +$830 |
| without discount / premium | −$2,670 | −$2,423 | −$248 |
| every kind of signal | −$1,458 | −$578 | −$880 |

The 50 days it was tuned on were its best stretch in 15 months; 7 of 16 months were up. Swing
lengths of 30, 75 and 100 bars all lost on the unseen months too.

**Confluence filters**, judged on June 2025 – January 2026 and checked once on February – mid-July
2026, on top of the setup above (executor, one MNQ contract):

| Added | Signals | Net a trade, design / holdout | Executor, design / holdout |
|---|---|---|---|
| nothing | 1,496 | −4.7 / −3.5 | −$360 / −$485 |
| ADX under 20 | 611 | −3.0 / −0.1 | −$113 / −$215 |
| outside yesterday's value area | 826 | −4.6 / −3.2 | −$15 / −$323 |
| close in the far half of yesterday's range | 969 | −4.3 / −4.9 | +$235 / −$390 |
| 1 SD past the session VWAP | 474 | −7.4 / +1.4 | −$758 / +$140 |
| RSI(14) 35 / 65 | 60 | −21.8 / +8.3 | −$288 / +$88 |
| Bollinger band touch | 207 | −11.1 / −0.1 | −$580 / −$35 |
| skip overnights over 1.5× their 20-day range | 1,261 | −4.2 / −5.5 | −$518 / −$645 |

Only ADX under 20 made each trade clearly better in both (the value area by a few tenths of a
tick); both are built in (see [ADX and yesterday's value area](#adx-and-yesterdays-value-area)),
and the built-in filters come out nearly the same: −3.0 / −0.4 ticks a trade with ADX under 20,
−4.1 / −3.3 outside yesterday's value area (its profile spread over 1-minute bars here, where the
test spread 10-second ones).

**Higher time frames.** The same FVG reactions on 5- and 15-minute bars, with 80-tick, 160-tick
and ATR-sized brackets (daily rules scaled to the stop): several 15-minute versions came out ahead
across June 2025 – September 2026, the ATR-sized one in every stretch (+$1,047). On the eight
months before that (27 October 2024 – 18 June 2025, 1-minute bars from ATAS as the path), which
played no part in choosing anything, they didn't hold: −$75 (ATR), −$583 (80 ticks), +$363 (160
ticks, where the 1-minute path flatters the break-even by several ticks a trade) and −$1,245 on
5-minute bars.

So on 23 months of MNQ no version of these signals has shown an edge that lasts beyond the data it
was chosen on. Buying and selling where price is stretched loses less; trading with a trend, and
judging on 1-minute bars, mislead.

## Honest limits

* The probability is a frequency from the history loaded on the chart, not a guarantee. Markets
  change; small samples stay close to the coin-flip odds by design.
* Fills are idealised: entry at the close (or exactly at the limit price), exact TP / SL, no
  slippage or commission. The backtest runner takes costs off.
* On historical bars the path inside a bar is unknown. It matters whenever a bar holds both the
  TP and the SL, or reaches +40 and also trades back to +20.
  * By default the indicator assumes price went from the open to the **nearer extreme first**,
    then to the other one and the close. TradingView's strategy tester makes the same assumption.
  * That is **optimistic whenever the break-even step fits inside a typical bar**. Price that
    reaches the trigger inside a bar often comes back to the moved stop before the close, and
    no order of a bar's high and low shows that. On the real ticks of 71 days of 1-minute NQ
    (see the backtest above), bars overstated the default 80 / 80 (break-even +40 → +20) by about
    2.6 ticks a trade, and a 40 / 40 bracket with break-even at +20 → +10 by about 4 in regular
    hours and 5 in their first two. Brackets without break-even came out the same on the bars as
    on the ticks, and ATR-sized ones within about a tick. Simulated random-walk paths
    (`tests/PathCheck`) show somewhat less: real prices come back inside a minute more often.
  * *Worst case* assumes the order that hurts the trade. With break-even on, that means any bar
    that reaches +40 from an open below +20 counts as stopped at break-even: on the real ticks,
    4–7 ticks a trade too pessimistic for both brackets. The truth usually lies between the
    two; the backtest runner shows both, and settles on ticks when you give it some.
  * Live bars follow the trades as they happen, so they don't have this problem. Reloading the
    chart can settle such a trade differently, and the odds on the labels come from history
    settled this way, so with a tight break-even step they lean optimistic too.
* Candlestick patterns are rules of thumb with fixed thresholds (wicks at most a tenth of the
  range, bodies against a 14-bar average...). They describe what the candles did, not what they
  will do.
* The order book is live only and aggregated per price. Iceberg orders that refill, or orders
  pulled a moment before price arrives, look like any other resting or pulled order.
* Signals close together often ride the same move. The cooldown limits that, but *n* can still
  overstate the independent evidence.
* Key levels and signal hours assume CME's session: a trading day that turns at 18:00 New York
  time, with US summer time. For another market, set *Regular hours start / end* to its session
  in New York time, or use *All hours*. The day still turns at 18:00 New York time.
* Sessions are read from each bar's open time, so they work best on minute charts. On 1-hour
  bars the bar that opens at 09:00 still counts as overnight, and there is no 30-minute opening
  range.
* Key levels come from the bars loaded on the chart: the first day has no prior day, and a day
  that starts partway through gets partial ranges.

## Execution: sending the orders

The same indicator can send the orders of its own signals. It is **off by default** (*Execute
signals*), and when you switch it on it trades **on paper** (*Paper trading*, on by default): a
simulator inside the indicator fills the orders and nothing leaves ATAS. Read the
[backtest](#backtest-two-years-of-1-minute-nq) first: nothing tested was profitable after costs on
real ticks, and sending the orders doesn't change that. Paper trading is there to check what the
executor *does* - which signals it takes, where its orders go, how they end - before any of it is
real.

### What it does

It decides nothing the chart has not:

* **Which signals.** Only the ones the chart shows, in real time. The *Signal hours*, *One trade at
  a time*, *Min TP probability* and *Min expected ticks* therefore apply to the orders exactly as they
  do to the chart. Signals in the history loaded on the chart are never traded. With *Filter trades by
  worst-case odds* on, a signal is only taken when its odds settled with the worst case inside each
  bar pass those two filters as well (see [below](#worst-case-odds)).
* **The entry.** With *Entry: At the signal bar's close*, a market order as soon as the bar has
  closed. With a limit entry, a limit order at the signal's own price; it is cancelled when the
  chart's order expires (*Limit order valid*).
* **The bracket.** Once filled, the signal's own stop and take profit, from whichever *Bracket size*
  is on (fixed ticks, multiples of the ATR, or beyond the signal bar), the stop sent first, as two
  plain orders: when one fills, the executor cancels the other. There is no OCO group - on
  connections without OCO at the broker, Rithmic among them, ATAS keeps OCO pairs on the computer,
  and live on 2026-09-28 it held a stop back from the broker. When the trade reaches its break-even trigger, the stop moves to its break-even
  price - or, with *Break-even stop from the fill* on, to the same profit counted from the
  position's average fill, so slippage on the entry doesn't come off it.
* **The exit.** The stop or the take profit, or at market as soon as the chart's trade ends while
  the position is still open: a take profit price only touched, *Max bars in trade*, *Close trades
  at session end*. The position never outlives the chart's trade.
* **The flat-by time** (off): with *Flat by (New York)* set, e.g. 15:55, the position closes at market
  and a waiting entry is cancelled at that time each trading day, whatever the chart's trade does
  next, and no new entry goes in during the last *No new entries in the last (minutes)* (10) before it,
  until the trading day turns at 18:00. A position still open when the day turns (a flat-by time in
  CME's 17:00-18:00 break, when nothing trades) closes at the new day's first price.
* **One position at a time**, even with *One trade at a time* off: a signal that comes while a
  position is open is logged as skipped.
* **One live trade per account**, across the charts of one ATAS: a live entry first takes the account,
  and another chart's signal on the same account is skipped until that position is done, or the
  indicator holding it is removed. Paper trading doesn't touch an account and is never held back.
* **The size**: *Contracts per trade* (1).

The executor follows its own copy of the signal's trade through the same prices and rules as the
chart, so a recalculation (after a settings change, say) never changes what happens to a position
that is already open. Switching *Execute signals* off stops new entries; an open position is still
managed until it ends.

### The daily loss limit

*Daily loss limit ($)* stops new entries for the rest of the trading day once the day's closed
trades have lost that much, commission included. The trading day turns at 18:00 New York time, like
the key levels. *Skip trades that could breach the limit* (on) also skips a signal whose stop, with
slippage and commission, could take the day past the limit, so a single trade can't blow through it.
The day's result is read back from the log when ATAS restarts, so a restart doesn't reset it. Paper
and live count apart.

Live, it also goes by the account's closed P&L for the session as the trading connection reports it
(*Count the account's closed P&L (live)*, on), whichever is worse, so trades by hand and on other
charts count too - for the limit itself and for skipping trades that could breach it. That can only
make the limit stricter. Where the session is cut is the connection's business, usually the same
18:00 New York for CME futures. Paper trading counts its own trades only. Your broker's or prop
firm's own limit still applies, and each computer only reads its own log: run the executor on one
computer at a time.

### The daily profit target

*Daily profit target (ticks)* (140) ends the trading day the other way: once the day's closed trades
have made that many ticks - a contract, slippage included - no new entries until the trading day
turns at 18:00 New York. A `TARGET` row and an alert say so, the panel says *done for today*, and a
restart reads the day's ticks back from the log. Paper and live count apart; 0 switches it off.

### Tick value and commission

The dollars of each trade - for the log, the daily loss limit and a new trade's risk - come from the
contract on the chart. With *Tick value and commission from the instrument* (on), the executor knows:

| Contracts | Tick | $ a tick | Commission a round trip |
|---|---|---|---|
| NQ / MNQ | 0.25 | 5.00 / 0.50 | 5.00 / 1.50 |
| ES / MES | 0.25 | 12.50 / 1.25 | 5.00 / 1.50 |
| YM / MYM | 1 | 5.00 / 0.50 | 5.00 / 1.50 |
| RTY / M2K | 0.10 | 5.00 / 0.50 | 5.00 / 1.50 |
| CL / MCL | 0.01 | 10.00 / 1.00 | 5.00 / 1.50 |
| GC / MGC | 0.10 | 10.00 / 1.00 | 5.00 / 1.50 |

It reads the product from the chart's instrument whatever the feed calls it (`NQZ6`, `MNQZ26`,
`NQZ6.CME@RITHMIC`, CQG's `F.US.ENQZ26`), and only trusts it when the chart's tick size is the
contract's. The commissions are typical all-in round trips (broker, exchange and clearing fees);
brokers differ, so with yours far off, switch the setting off and set *Tick value* and *Commission*
by hand - as for any other instrument. When the trading connection reports a larger tick value, that
is used, so a wrong value can only make the limit stricter. The MODE row says whose costs are in use.
The brackets are in ticks, and NQ and MNQ share their price scale: the same settings trade both the
same way, for a tenth of the dollars on MNQ.

### Worst-case odds

On historical bars the order of a bar's high and low is unknown, and with a break-even step inside a
bar the chart's default rule (the nearer extreme first) settles trades optimistically: by about 2.6
ticks a trade for the default bracket on the backtest's real ticks. The labels' odds come from that
history, and so do *Min TP probability* and *Min expected ticks*, which pick the signals the executor
takes.

So every trade is also settled, on a copy, with the worst case inside each bar (the stop first
whenever the order would decide it), into odds of their own; the labels keep theirs. With *Filter
trades by worst-case odds* on, the executor only takes a signal whose worst-case odds pass *Min TP
probability* and *Min expected ticks* too (with both filters off it changes nothing). The worst case
errs the other way - 4-7 ticks a trade too low on the backtest's ticks - so a signal that passes it
has a margin. Every log row that names a signal carries both EVs (`ev_ticks`, `ev_worst_ticks`).

### Paper fills

Market and stop orders fill *Slippage* ticks (1) worse than the price that reached them, and a stop
that price gaps through fills at the gap's price. Limit orders fill at their own price once price
trades a tick through them, as the chart's limit entries do. So a take profit that price only
touches doesn't fill: the chart counts it, and the position closes at market with the chart's
trade, usually a tick lower. Paper results are therefore a little worse than the chart's, as real
fills usually are.

### Going live

With *Paper trading* off, orders go to ATAS's trading connection (the chart's account and
instrument) only when all of these hold; otherwise each signal is logged as skipped, with the reason:

* *Live account* is the account selected on the chart (type its ID; case and spaces don't matter);
* a *Daily loss limit* is set;
* the chart's instrument has the trading instrument's tick size;
* ATAS's own automatic stop loss / take profit (SL/TP) is off: its orders would work against the
  executor's - its take profit a tick in front of the executor's closes winners behind its back, and
  two stops can both fill;
* the account holds no position in the instrument.

The panel says *not armed* and why. Orders go without ATAS's confirmation dialog, and without its
question about earlier orders still unanswered (`checkOrderStates`), which would hold an order until
someone clicks. If ATAS or the broker rejects the stop or the take profit, the position is closed at
market and the executor **halts** (no new entries until *Execute signals* is switched off and on
again). So does a stop, take profit or closing order the broker hasn't confirmed 5 seconds after it
went out: it is asked to cancel in case it still turns up, it is cancelled again if it turns up
working, and one that fills after all halts the executor with a warning - the account may then hold
a position the indicator doesn't know of. So does a stop
that goes away without the executor asking (cancelled by hand, or expired at the broker), after a
few seconds that leave room for a take profit filling at the same moment. A position that
disappears from the account (closed by hand, say) halts it too, and so does a closing order that is
rejected, which you then have to close yourself. Removing the indicator cancels an entry that is still waiting; an
open position keeps its stop and take profit at the broker, but nothing moves or closes it any more.

A signal is also skipped while another chart holds a live trade on the same account (one live trade
per account at a time).

*Time in force (live orders)* sets how long the stop, the take profit and a limit entry stay at the
broker: the connection's default (as ATAS sends orders), Day, or Good till cancelled; market orders
are left alone. A stop that expires counts as cancelled - the position is closed and execution halts -
so a position that may be held through the session break wants Good till cancelled, or a *Flat by*
time before the break. Some connections only take Day orders, and a refused stop also closes the
position and halts: try the setting on a simulated account first.

The live side has been tested against a simulated broker only (see [tests/README.md](tests/README.md)),
not against ATAS's own connection. Before it trades a funded account, run it where mistakes are free:

1. **Paper trading on a live chart**, for weeks. Then sum the log up with the
   [log report](tests/README.md#logreport): the trades by setup and confirmation, and the labels'
   odds against what happened. The backtest's bars have no footprint, so this is the only test the
   fill, delta and order-flow signals get.
2. **ATAS's Market Replay, or a simulated account, with *Paper trading* off**: the real order calls,
   through ATAS's own connection. Check the log against what the broker shows, fill for fill.
3. On that simulated account, **break things on purpose**: close the position by hand, cancel its
   stop, disconnect the feed and reconnect, restart ATAS with a position open. Each should end as
   described above - a halt, or the position left with its stop and take profit at the broker.
   Check that no position is ever left without a stop.
4. **Live settings**: a *Daily loss limit* that fits the account, a *Flat by* time if the position
   mustn't be held into the evening, the *Time in force* the connection takes, and one contract to
   start.
5. **The same ATAS version on every computer** that builds the indicator, and the executor on one
   computer at a time. It was written against ATAS X 8.0.15; classic ATAS 8.0.14 builds it too (see
   [Install](#windows-classic-atas-platform)), but only the steps above show that its trading
   connection behaves the same.

### The log

Every signal the executor sees, every order, fill, cancel and stop move, and every result goes to a
CSV file: one per trading day, instrument and mode (`2026-09-25_NQ_paper.csv`), in *Log folder*,
by default `ATAS/FvgExecution` in the application data folder (on a Mac
`~/Library/Application Support/ATAS/FvgExecution`, on Windows `%APPDATA%\ATAS\FvgExecution`).
Every row names the signal behind it - its time and side (`20260925-094900-BUY`, as its tooltip
shows it), setup, patterns, confirmations and the odds on its label - so you can line it up with the
chart and the scoreboard:

| Event | When |
|---|---|
| `MODE` | the settings in use, at the start of each file and whenever they change |
| `SIGNAL` / `SKIPPED` | a real-time signal was taken, or not, and why |
| `ORDER`, `MODIFY`, `CANCEL`, `CANCELLED`, `REJECTED` | an order sent, moved (break-even) or resized, cancelled, refused |
| `FILL` | an entry or exit fill, with its price and the position after it |
| `EXIT` | closing at market, and why |
| `CLOSED` | the position is flat: ticks a contract, $ after commission, the day's $ so far, and the chart's result for the same signal |
| `CHART` | how the chart's trade for the signal ended, as its tooltip says it |
| `BREAKER`, `TARGET`, `HALT`, `WARN`, `RESTORE`, `NOFILL` | the daily loss limit reached, the daily profit target made, a halt, a warning, the day read back after a restart, an entry that never filled |

With execution on, the panel adds a line for the executor (mode, size, the day's result against the
limit), one for the open position and one for the last thing it did.

A day's file started by an earlier version keeps its own columns, so after an update a restart still
reads the day back. To sum the logs up - the trades by setup, confirmations, hour and the odds on
their labels, next to what the chart's own trades made - run
`dotnet run -c Release --project tests/LogReport` (see [tests/README.md](tests/README.md#logreport)).

### Limits of the executor

* The chart's *Close trades at session end* acts on the first price of the next session, after
  CME's daily break, so a position can be held through it. To be flat by a given time, set *Flat by*;
  the executor still has no clock of its own, and acts on the first price at or after that time.
* A live limit entry can fill on a touch that the chart doesn't count as a fill. Unless price then
  trades through it, the position closes at market when the chart's order expires. That keeps the
  executor to trades the chart counts: holding such a position on with its bracket would trade what
  the statistics never saw, for no expected gain.
* The break-even trigger is the chart trade's; *Break-even stop from the fill* only changes where the
  stop moves to.
* A stop that expires at the broker counts as cancelled: the position is closed and the executor
  halts. *Time in force* sets how long the orders stay.
* One live trade per account holds across the charts of one ATAS, not across computers or other
  programs; the account's closed P&L in the daily loss limit covers those, once their trades close.

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
   The build reads that ATAS: it makes the DLL for the .NET it runs on (`-p:TargetFrameworks=net8.0-windows`
   makes another one), and uses the chart time zone it has - `TimeZoneOffset`, or on older versions
   such as 8.0.14 `TimeZone` in hours.
   Or drop `FvgReactionLiquiditySweep.cs` into an indicator project you already have (on ATAS 8.0.14
   and older, define `ATAS_TIMEZONE_HOURS` there).
2. Copy `bin\Release\net10.0-windows\FvgReactionLiquiditySweep.dll` into `%APPDATA%\ATAS\Indicators`
   (or `Documents\ATAS\Indicators`). ATAS builds that still run on .NET 8 get the DLL in
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
| | Min sweep depth (x ATR, 0 = off) | 0 | how far beyond the high / low a sweep has to trade, in ATRs of the bars before it |
| | Sweep confirmed swings only | off | only swing highs / lows (3 bars each side) nothing has traded beyond since; each counts once |
| Fair Value Gap | Min FVG Size (ticks) | 4 | |
| | Min FVG size (x ATR, 0 = off) | 0 | a gap must also be this many ATRs of the bars before it |
| | Zone Max Age (bars) | 150 | a gap nothing used or filled within this many bars stops being watched |
| | Require reaction close beyond zone | on | a bullish reaction must close above the gap, a bearish one below it |
| | FVG is filled when | Price reaches its far edge | or: a candle closes beyond it; price reaches its middle (50%) |
| | Draw FVG Zones, Show zone midline (50%) | on | |
| | Show used / filled zones | off | a faint box for each gap that is no longer watched |
| | Bullish zone color, Bearish zone color | translucent teal / red | |
| Sessions & Key Levels | Signal hours (New York) | Regular hours | or all hours; the first two hours of regular hours; regular hours but not 11:30 – 13:30; a custom window |
| | Custom window start / end (New York) | 09:30 / 10:30 | with a custom window; one that ends before it starts runs over midnight, the same time for both means all day |
| | Regular hours start / end (New York) | 09:30 / 16:00 | for the signal hours, the prior day, the overnight range and the opening range |
| | Opening range (minutes) | 30 | |
| | Prior day high / low, Overnight high / low, Opening range high / low, Equal highs / lows | on | which key levels to watch |
| | Equal level match (ticks) | 2 | how far apart two swing highs (lows) may be and still count as equal |
| | Equal level lookback (bars) | 120 | how many bars apart the two swings may be, and how long the level counts after the second one |
| | Draw key levels | on | the lines; their sweeps count either way |
| Order Flow | Show big fills | on | the bubbles |
| | Big fill size | Among the biggest of recent bars | or a fixed number of contracts |
| | Top share of recent bars (%) | 10 | the bar's busiest price must rank this high among the busiest prices of recent bars |
| | Fill lookback (bars) | 200 | the recent bars it is ranked against |
| | Min filled volume at one price | 150 | with a fixed size: contracts at the bar's busiest price |
| | Filled volume vs bar average (x) | 3.0 | and this many times the bar's average per price |
| | Reaction window (bars) | 3 | bars after the fill bar a reaction may take |
| | Show resting orders | on | live, from Level 2 |
| | Resting order size | A fixed number of contracts | or times the typical level in the book |
| | Min resting order (contracts) | 70 | at one price, with a fixed size |
| | Resting order vs typical level (x) | 5.0 | times the median size of the prices in the book |
| | Filled when traded (%) | 50 | share of an order that must trade at its price for it to count as filled rather than pulled |
| | Show pulled orders | off | a faint trace of big orders that were cancelled |
| Signals | Buy signals, Short signals | on | |
| | Signal source | FVG reaction, fill reaction or sweep | or FVG reaction only, liquidity sweep only, sweep then FVG reaction, fill reaction only, key-level sweeps only (alone or then FVG). Wherever sweeps count, key sweeps do too |
| | Sweep -> FVG window (bars) | 10 | |
| | Trend EMA period (0 = off) | 50 | |
| | Only trade with the trend / Require delta / Require order-flow confirmation / Require candlestick pattern | off | |
| | Only trade with the day's trend | off | BUYs only above the day's open and VWAP, SHORTs only below both, none in between |
| | Buy in discount, short in premium | off | BUYs only below the middle of the last swing range, SHORTs only above it |
| | Premium / discount swing length (bars) | 50 | a swing high has no higher high within this many bars either side; it counts once they have closed |
| | Max ADX (0 = off) | 0 | signals only while Wilder's 14-bar ADX is below it |
| | Only outside yesterday's value area | off | BUYs only below the last regular session's value area low, SHORTs only above its high |
| | Skip the day after a quiet one (% of usual range, 0 = off) | 0 | no signals on a day after a regular session under this share of the average of the 20 before it |
| | Cooldown between signals (bars) | 3 | per direction |
| | One trade at a time | on | new signals while a trade is open are hidden but still learned from |
| | Min TP probability to show (%) | 0 | hide signals with lower TP odds; they are still learned from |
| | Min expected ticks to show (0 = off) | 0 | hide signals with a lower EV; they are still learned from |
| | Volatility (ATR) period (bars) | 20 | the ATR behind the sweep depth, the gap size, ATR brackets and the volatility split of the odds |
| Candlestick Patterns | Hammer / Shooting star, Inverted hammer / Hanging man, Engulfing, Piercing line / Dark cloud cover, Harami, Tweezer bottom / top, Morning star / Evening star, Three white soldiers / black crows, Marubozu, Three inside up / down, Three outside up / down, Outside reversal, Three-line strike | on | one switch per pattern pair; the doji stars go with the stars, the dragonfly / gravestone dojis with the hammers |
| | Hammer wick / body (min) | 2 | for hammers, shooting stars, inverted hammers and hanging men |
| | Average body (bars) | 14 | the yardstick for long and small bodies |
| | Tweezer match (ticks) | 1 | how far apart a tweezer's two lows (highs) may be |
| Entry | Entry | At the signal bar's close | or a limit order on a pullback |
| | Pullback (% of the signal bar) | 25 | with a limit entry: how far back from the close the order sits |
| | Limit order valid (bars) | 3 | bars after the signal bar the order waits; unfilled, it is cancelled and left out of the results |
| Take Profit / Stop Loss | Bracket size | Fixed ticks | or multiples of the ATR; or the stop beyond the signal bar |
| | Take profit (ticks) / Stop loss (ticks) | 80 / 80 | with fixed ticks |
| | Take profit (x ATR) / Stop loss (x ATR) | 3 / 3 | with the ATR bracket |
| | Stop buffer (ticks) | 2 | with the stop beyond the signal bar: how far beyond its low (buys) / high (shorts) |
| | Take profit (x the stop) | 1.5 | with the stop beyond the signal bar: the reward : risk |
| | Min stop / Max stop (ticks) | 16 / 200 | with the ATR or signal-bar bracket; the take profit keeps its ratio |
| | Break-even trigger (ticks, 0 = off) | 40 | with fixed ticks: profit at which the stop moves |
| | Break-even stop (ticks in profit) | 20 | with fixed ticks: where it moves to (0 = the entry price); kept below the trigger |
| | Break-even trigger / stop (% of TP) | 50 / 25 | the same with the ATR or signal-bar bracket, as shares of the take profit (trigger 0 = off) |
| | Max bars in trade (0 = no limit) | 0 | counted from the fill; expired trades are left out of the probabilities |
| | Close trades at session end | off | also takes no new signal on a session's last bar |
| | Order of high and low inside a bar | Open to the nearer extreme first | or worst case for the trade, or candle direction (O-L-H-C / O-H-L-C) |
| | Probability smoothing (virtual trades) | 10 | higher = steadier numbers that need more history to move |
| | Odds by time of day / by volatility / by trend | off | split each setup's odds by when the signal came, how busy the market was, or whether it went with the trend EMA |
| | Odds by confirmations | on | split each setup's odds by its number of confirmations |
| Display | Show signal labels, Compact labels for closed trades, Show TP / SL levels, Show statistics panel | on | closed trades keep a small result chip and a faint box |
| | Keep the scoreboard open | off | shows the scoreboard without hovering the panel |
| | Show reaction labels | off | name the pattern next to each reaction marker (hovering one always does) |
| | Statistics panel position, Label offset (px), Label font, Take profit / Stop loss / Break-even line | top right, 24, Arial 9 | |
| Alerts | Alert on new signal / Alert on TP / SL / break-even / Alert sound file | off / off / alert1 | the second also fires when a stop moves to break-even; only in real time, never while history loads |
| Execution | Execute signals | off | send the orders of the signals shown, in real time only |
| | Paper trading (simulated fills) | on | off: real orders, only once armed (below) |
| | Live account (must match the chart's) | empty | live orders only go to this account, while it is the chart's |
| | Contracts per trade | 1 | |
| | Daily loss limit ($, 0 = off) | 0 | no new entries for the rest of the trading day once the day's closed trades lost this much; live trading needs one |
| | Skip trades that could breach the limit | on | also skip a signal whose stop could take the day past the limit |
| | Daily profit target (ticks, 0 = off) | 140 | no new entries for the rest of the trading day once the day's closed trades made this many ticks a contract |
| | Count the account's closed P&L (live) | on | the limit also goes by the account's closed P&L for the session, when that is worse |
| | Flat by (New York, 00:00 = off) | off | close the position and cancel a waiting entry at this time each trading day |
| | No new entries in the last (minutes) | 10 | with a flat-by time: no entries this long before it, until the day turns at 18:00 |
| | Filter trades by worst-case odds | off | a signal's worst-case odds must pass Min TP probability and Min expected ticks too |
| | Break-even stop from the fill | off | the break-even stop keeps its profit from the average fill, not from the signal's price |
| | Time in force (live orders) | the connection's default | or Day, or Good till cancelled, for the stop, the take profit and a limit entry |
| | Tick value and commission from the instrument | on | NQ, MNQ, ES, MES and the other contracts [above](#tick-value-and-commission) get their own |
| | Tick value ($ per contract) | 5 | other instruments, or the setting above off: NQ 5, MNQ 0.5. The larger of this and the connection's is used |
| | Commission ($ per contract, round trip) | 5.00 | other instruments, or the setting above off: taken off each trade's result |
| | Slippage (ticks per market / stop fill) | 1 | paper fills, and the risk of a new trade |
| | Alert on orders | on | an alert per fill and per closed position; halts, rejections and the loss limit always alert |
| | Log folder | ATAS/FvgExecution in the application data folder | the CSV log |

The signal arrows are regular data series (*Buy Signal*, *Short Signal*), so ATAS can also use
them for alerts or automation. The reaction and sweep series (*FVG Bull Reaction*, *Liquidity
Sweep Low*...) keep their names and values for the same purpose, but are hidden, since the chart
draws its own markers for them. The sweep series also mark sweeps of key levels.

## Changes in this version

New in this version. The execution is off by default, and nothing that came with it changes the
chart's signals unless you switch it on:

* **execution**: the indicator can send the orders of its own signals, paper by default, with
  position sizing, a daily loss limit, live orders only once armed for the chart's account, and a
  CSV log of every order. See [Execution](#execution-sending-the-orders). Since then:
  * **safety**: one live trade per account across charts; live, the account's closed P&L counts
    toward the daily loss limit (on); an optional flat-by time; time in force for live orders; an
    optional break-even stop counted from the fill;
  * **live orders on Rithmic**: the first live trade (2026-09-28) showed ATAS holding the stop back -
    it was in an OCO group, which ATAS keeps on the computer for Rithmic - so the take profit never
    went out and the break-even move was refused. The stop and take profit are now two plain orders,
    each call goes out without waiting on the one before or on ATAS's dialogs, an order the broker
    hasn't confirmed within 5 seconds closes the position and halts, and live trading isn't armed
    while ATAS's own SL/TP is on;
  * **one order change at a time**: on Rithmic ATAS changes an order by cancelling it and
    registering a replacement. On 2026-10-01 a take profit filling in pieces made the stop's resize
    and its cancel overlap: the cancel went to a replacement not registered yet, and the stop was
    left working with no position. Now a change waits for the one before it, a cancel waits for the
    replacement, a change not registered within 5 seconds counts as rejected, and once the position
    is flat a cancel still unanswered is sent again (3 seconds apart, three in all) before a halt;
  * **worst-case odds**: every trade is also settled with the worst case inside each bar, the log
    carries that EV next to the label's, and the executor can filter on it;
  * **the log report** (`tests/LogReport`) sums the executor's logs up;
  * **Windows**: the build makes the DLL for the installed ATAS only, and reads which chart time
    zone property that ATAS version has, so classic ATAS 8.0.14 builds it;
  * **the day's trend** (off): signals only go the day's way - above the day's open and VWAP for
    BUYs, below both for SHORTs. It came out on, and is off since it made the signals worse on 50
    days of MNQ;
  * **premium and discount** (off): BUYs only in the lower half of the last swing range, SHORTs
    only in the upper half, after joshyattridge/smart-money-concepts - the one idea from it that
    helped on 50 days of MNQ, see [Premium and discount](#premium-and-discount);
  * **Max ADX and yesterday's value area** (off): signals only in a quiet market, or only outside
    the last regular session's value area - the two confluences that cut the losses on months the
    setup was never tuned on, see [ADX and yesterday's value area](#adx-and-yesterdays-value-area);
  * **skip the day after a quiet one** (off): no signals after a regular session with an unusually
    narrow range; FOMC and CPI days, and choppy days, were tested too - see
    [Quiet days and news days](#quiet-days-and-news-days);
  * **a daily profit target** (+140 ticks): no new entries for the rest of the day once made;
  * **the instrument's own costs** (on): NQ, MNQ, ES, MES and other CME contracts get their own
    tick value and a typical commission;
  * **FVG Bar Export**: a second indicator that writes a chart's bars and footprint for the
    backtest runner, which now reads delta and (`--footprint`) the footprint too - see
    [On ATAS's own data](#on-atass-own-data). The runner reads 1-second bars as the path inside
    each minute (`--ticks`), so an export from a 1-second chart settles trades as ticks would.

New in the previous version, all off by default:

* **signal filters**: a custom window for the signal hours, a minimum sweep depth and gap size in
  ATRs, and sweeps of confirmed swing highs / lows only;
* **brackets that follow the market**: multiples of the ATR, or the stop just beyond the signal
  bar with a reward : risk, each with its break-even step as a share of the take profit;
* **limit entries** on a pullback into the signal bar;
* **odds split** by time of day, volatility and trend, as well as by confirmations;
* the **backtest runner** takes commission and slippage off, reports two stretches apart
  (`--split`), replays the history with the worst-case order inside each bar next to the result,
  and settles trades on real ticks (`--ticks`). See [Backtest](#backtest-two-years-of-1-minute-nq)
  for what two years of NQ said about all of this.

New since the rewrite, in the version before:

* **key levels**: the prior day's, the overnight and the opening range's highs / lows and equal
  highs / lows, swept or broken, with two new triggers, *Key sweep* and *Key sweep+FVG*;
* **signal hours**: by default signals only fire during regular hours, in New York time;
* **big fills sized against recent bars** by default instead of a fixed 150 contracts, and an
  optional resting-order size relative to the book;
* the **scoreboard** per setup and per pattern, with the odds check;
* the Windows DLL for ATAS builds that run on .NET 8 now compiles (that target was missing
  `System.Drawing.Common`).

For the signals of the rewrite, set *Signal hours* to *All hours* and *Big fill size* to *A fixed
number of contracts*, and switch the four key levels off.

The rewrite itself:

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
`tests/ExecutionTests` checks the execution side: paper orders and exits, the daily loss limit, the
log, and the live order calls against a simulated broker.
`tests/Backtest` replays a CSV of bars through the same code, for a backtest over any stretch of
history outside ATAS, `tests/PathCheck` measures how far a backtest on 1-minute bars is off
for given settings, and `tests/LogReport` sums the executor's logs up. They are not part of the
indicator build.
