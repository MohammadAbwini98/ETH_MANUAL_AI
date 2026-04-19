Act as a senior .NET 9 / C# trading-platform engineer working directly inside the existing repository:

Repository:
MohammadAbwini98/ETH_MANUAL_AI

Objective:
Implement Capital.com DEMO trade execution for the existing ETH signal system in a loosely coupled way, using the current signal recommendations already produced by the system and shown in the existing dashboard.

Important repository context you must respect:
1. This is an existing multi-project .NET solution, not a greenfield build.
2. Keep the current architecture style:
   - EthSignal.Domain
   - EthSignal.Infrastructure
   - EthSignal.Web
3. Extend the existing broker integration instead of replacing it.
4. Do not break the current signal engine, ML flow, replay flow, blocked/generated signal history, or current dashboard.
5. Build the order-execution subsystem as a separate module/service layer that is wired to signals via interfaces/events/contracts, not tightly embedded inside the signal engine.

Current implementation facts you must first verify in code before changing anything:
1. Existing signal model already includes:
   - SignalId
   - Symbol
   - Timeframe
   - SignalTimeUtc
   - Direction
   - EntryPrice
   - TpPrice
   - SlPrice
   - ConfidenceScore
   - Regime
   - Reasons
   - Status
2. Existing Capital client currently supports:
   - auth/session
   - spot price
   - candles
   - sentiment
   but not full trade execution lifecycle.
3. Existing dashboard already has:
   - latest signal panel
   - signal history
   - blocked signals history
   - generated signals history
   - performance summary
   - ML sections
4. Existing backend already exposes many API endpoints from Program.cs and uses minimal APIs.

Your task:
Implement a production-style DEMO-only trade execution module for Capital.com and wire it to the current signal system.

Critical business rules:
1. ALL orders must go ONLY to the Capital.com DEMO account.
2. No live account usage under any condition.
3. Trades may be executed from:
   - Recommended signals
   - Generated signals
   - Blocked signals
4. The executed trade record must always preserve the original signal category:
   - Recommended
   - Generated
   - Blocked
5. The dashboard must clearly show which one it was.
6. Each executed record must show the original SignalId and support force-close.

Architecture requirements:
1. Keep it loosely coupled.
2. Do NOT put Capital.com-specific DTOs into the signal domain model.
3. Do NOT make the dashboard call Capital.com directly.
4. Do NOT hardwire order placement inside the core signal-generation engine.
5. Add a broker execution layer with interfaces such as:
   - ITradeExecutionService
   - ITradeExecutionPolicy
   - ICapitalTradingClient
   - IExecutedTradeRepository
   - IExecutionCandidateMapper
   - IAccountSnapshotService
6. Existing signal generation should publish or expose execution candidates.
7. Execution should consume a normalized internal trade-candidate contract, not raw signal engine internals.
8. Make the broker layer replaceable later.

Capital.com API requirements:
Use the official Capital.com DEMO API flow and implement the following properly:
1. session creation / authentication
2. demo account validation
3. market / instrument lookup if needed
4. open position request
5. confirmation using deal reference
6. open positions retrieval
7. account metrics retrieval
8. close position / force-close
9. robust handling for:
   - expired session
   - auth failure
   - invalid size
   - invalid instrument
   - invalid TP/SL
   - insufficient funds
   - rejected order
   - timeout
   - confirmation mismatch
10. Never mark a trade as successfully opened until the confirm step has succeeded.

Before implementing, analyze the current project and document in code comments / implementation notes:
1. where signal recommendations are created
2. how EntryPrice is currently determined
3. how TP and SL are currently determined
4. how direction is determined
5. how timeframe is determined
6. how risk sizing is currently handled
7. how blocked/generated/recommended signal flows are currently represented
8. which existing APIs and dashboard sections should be extended
9. which existing abstractions can be reused
10. which parts must be refactored minimally to support loose coupling

Required implementation scope:

A) Extend broker integration
1. Extend the existing Capital client rather than creating a completely disconnected duplicate.
2. Add DEMO trading methods for:
   - PlacePositionAsync(...)
   - ConfirmDealAsync(...)
   - GetOpenPositionsAsync(...)
   - GetAccountSnapshotAsync(...)
   - ClosePositionAsync(...)
3. Keep price/candle/sentiment methods working.
4. Add session refresh and retry behavior safely.
5. Make demo/live separation explicit in config and fail startup or execution if the target is not demo.

B) Add execution domain / models
Create clean internal models for:
1. TradeExecutionCandidate
2. TradeExecutionRequest
3. TradeExecutionResult
4. ExecutedTrade
5. ExecutedTradeStatus
6. SignalExecutionSourceType
7. AccountSnapshot
8. ForceCloseRequest / ForceCloseResult

These must be internal application models and not Capital.com raw DTOs.

C) Add persistence
Add database entities/tables and repositories for:
1. executed_trades
2. execution_attempts
3. account_snapshots
4. execution_events / audit trail
5. close_trade_actions
6. optional platform raw response log table if useful

Each executed trade record should persist at minimum:
- executedTradeId
- signalId
- signal source type: Recommended / Generated / Blocked
- symbol / instrument
- timeframe
- direction
- recommended entry
- actual execution entry
- TP
- SL
- requested size
- executed size
- capital deal reference
- capital deal id if available
- status
- opened at
- closed at
- outcome
- pnl
- account currency
- failure reason
- error details
- force-closed flag
- close source (user/system/tp/sl/platform)

D) Determine execution eligibility
Implement an execution policy layer that evaluates whether a signal can be executed:
1. signal is recent and not stale
2. direction is BUY/SELL
3. entry/tp/sl are valid and directionally correct
4. symbol is supported
5. instrument maps correctly to Capital.com market identifier
6. size resolves to a valid minimum executable amount
7. duplicate execution is prevented
8. there is enough account margin/funds
9. blocked/generated/recommended source is recorded
10. execution reason is auditable

E) Entry / TP / SL handling
Analyze the current signal recommendation logic and adapt execution safely:
1. Determine whether order should be:
   - market now at current price
   - market only if near recommended entry
   - rejected if price drift exceeds tolerance
2. Make this configurable.
3. Use current repo logic for entry/TP/SL as the source of truth unless invalid for live demo execution.
4. If entry must be re-anchored to actual fill price, preserve both:
   - recommended entry
   - actual fill entry
5. Validate TP/SL directionally:
   - BUY => TP > entry and SL < entry
   - SELL => TP < entry and SL > entry
6. Validate precision/rounding before sending to broker.

F) Sizing
1. Use the minimum valid Capital.com size for the selected instrument at first.
2. Do not assume a hardcoded amount without validating instrument rules.
3. If the current signal model does not carry tradable size, derive it through a dedicated sizing policy.
4. Preserve both requested and final executed size.

G) Signal-to-execution wiring
Implement loose coupling:
1. Add a mapper from SignalRecommendation to TradeExecutionCandidate.
2. Add dedicated mapping for generated and blocked signal records too.
3. Do not embed execution logic directly inside SignalEngine.
4. Prefer:
   - application service orchestration
   - queued/background execution workflow
   - or explicit execution endpoint/service
5. Make it possible for dashboard/manual action and system auto-execution to use the same execution service.

H) Dashboard changes
Extend the existing dashboard and current frontend files instead of replacing them.

Add a new dashboard section:
Executed Signals / Trades

It must include:
1. table columns:
   - SignalId
   - Source Type (Recommended / Generated / Blocked)
   - Instrument
   - Direction
   - Timeframe
   - Recommended Entry
   - Executed Entry
   - TP
   - SL
   - Size
   - Status
   - Open Time
   - Close Time
   - PnL
   - Currency
   - Deal Reference
   - Failure Reason
   - Force Close button
2. filters:
   - from date
   - to date
   - instrument
   - direction
   - timeframe
   - signal source type
   - execution status
3. widgets for:
   - available balance
   - equity
   - funds
   - margin
   - currency
   - open trades
   - total executed
   - wins
   - losses
   - total pnl
   - win rate
   - failed executions
4. show useful broker diagnostics:
   - demo account indicator
   - last sync time
   - session status
   - latest broker error
   - latest order response note

I) API endpoints
Add minimal APIs consistent with the current Program.cs style for:
1. GET /api/executed-trades
2. GET /api/executed-trades/{id}
3. POST /api/executed-trades/execute-signal/{signalId}
4. POST /api/executed-trades/execute-generated/{signalId}
5. POST /api/executed-trades/execute-blocked/{signalId}
6. POST /api/executed-trades/{id}/force-close
7. GET /api/trading/account-summary
8. GET /api/trading/open-positions
9. GET /api/trading/execution-stats
10. GET /api/trading/health

Keep API response structure consistent with the current project style.

J) Auto-execution capability
Implement system capability to execute:
1. recommended signals
2. generated signals
3. blocked signals

But do this through a configurable execution policy:
- enabled/disabled
- allowed source types
- stale window
- entry drift tolerance
- max concurrent open trades
- demo-only guard

K) Error handling
Handle and persist all broker/API failure scenarios.
Every failure must produce:
1. technical log
2. persisted execution attempt record
3. human-readable dashboard message where appropriate
4. no false “executed” state

L) Testing
Add tests matching current project style:
1. unit tests for signal-to-execution mapping
2. unit tests for TP/SL validation
3. unit tests for duplicate prevention
4. unit tests for source-type preservation
5. integration tests for:
   - auth/session
   - place + confirm
   - account summary
   - close position
6. API endpoint tests
7. dashboard-facing contract tests if practical

M) Safety rules
1. Demo account only.
2. Never place live orders.
3. Never treat order placement response as final success without confirmation.
4. Never tightly couple the trade executor to SignalEngine internals.
5. Do not remove existing signal history, blocked history, generated history, ML, or replay capabilities.
6. Prefer additive, low-risk, maintainable changes.

Expected deliverables:
1. Full code implementation
2. Minimal necessary schema changes
3. New repositories/services/interfaces
4. New or updated API endpoints
5. Dashboard updates
6. Tests
7. A short implementation note describing:
   - what existing files were extended
   - what new files were added
   - how signal recommendations are converted into executable broker orders
   - how demo-only safety is enforced
   - how the design remains loosely coupled

Implementation preference:
Make the smallest clean architectural change that fits the current repository structure, naming, endpoint style, and dashboard implementation.