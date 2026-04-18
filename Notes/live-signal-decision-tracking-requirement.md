# Live Signal Decision Tracking - Implementation Requirement

## Overview

Add a new dashboard section called **Live Signal Decision Tracking**.

This feature must provide **real-time tracking of the signal decisioning phase only**, starting from the moment a signal is detected and ending when the system reaches the final decision.

The purpose of this feature is to let the user see **how the system reached its decision**, step by step, based on the internal strategy rules, validations, filters, ML checks, and adaptive strategy checks.

This feature is intended for **decision transparency and debugging**, not for trade execution tracking.

---

## Main Objective

Display each detected signal in a live dashboard view and show the full decision path from:

- **Signal Detected**
- through all evaluation and filtering stages
- until **Final Decision Made**

The dashboard must clearly show:

- what rule was evaluated
- in what order it was evaluated
- whether it passed, failed, or was skipped
- what values were used in that evaluation
- why the final decision was made

---

## Scope

### In Scope

This feature must cover only the **signal decisioning lifecycle**, including:

1. Signal detected
2. Signal registered for evaluation
3. Basic validation checks
4. Strategy rule evaluation
5. Filter evaluation
6. Indicator-based confirmation or rejection
7. ML contribution or confidence evaluation
8. Adaptive strategy contribution
9. Final decision generation

### Out of Scope

This feature must **not** include:

- order creation
- order submission
- order execution
- fill / rejection status from exchange
- position opening
- position monitoring
- stop loss / take profit lifecycle
- trade closure lifecycle

Once the system reaches the final decision state, live tracking for that signal ends.

---

## Functional Requirement

### Dashboard Section

Add a dedicated dashboard section named:

**Live Signal Decision Tracking**

This section must show all signals currently under evaluation in real time.

If more than one signal is being evaluated at the same time, the dashboard must display all of them concurrently, with each signal shown independently.

---

## Core Behavior

### 1. Signal Detection Entry

When the strategy detects a raw signal, the system must immediately create a live decision tracking entry for that signal.

This entry must appear in the dashboard without requiring manual refresh, if the dashboard already supports live update mechanisms.

### 2. Step-by-Step Rule Evaluation

From the moment the signal is detected, the system must log and display each decisioning step in sequence.

Each evaluation event must be tied to the specific signal ID and displayed in the correct order.

### 3. Live Status Updates

The dashboard must update the signal view live as new rule evaluation events occur.

The user must be able to observe the signal moving from one decisioning step to the next in real time.

### 4. Final Decision State

The decisioning lifecycle ends when the system reaches one of the final decision states:

- **Accepted**
- **Rejected**
- **Ignored**
- **Blocked**

Once one of these states is reached, the signal’s live decision tracking is considered complete.

---

## Per-Signal Dashboard Data

Each live signal item must display at minimum:

- Signal ID
- Symbol / market pair
- Direction (Buy / Sell / Long / Short)
- Timeframe
- Strategy name or source module
- Detection timestamp
- Current decisioning stage
- Current status
- Final decision when available

### Optional but Recommended

- Confidence score
- Raw strategy score
- ML score
- Adaptive strategy mode
- Signal age / elapsed time since detection

---

## Rule-Level Tracking Requirement

Each signal must include a detailed list of its evaluated rules and filters.

For every rule evaluation event, display:

- Rule name
- Evaluation order / sequence number
- Result status:
  - Passed
  - Failed
  - Skipped
- Timestamp
- Optional numeric or textual values used during the evaluation
- Human-readable reason or explanation

### Example Rule Events

- Market Regime Check -> Passed -> Regime = Trending
- Trend Confirmation -> Failed -> Long trend confirmation not satisfied
- RSI Confirmation -> Passed -> RSI = 28.7
- ML Confidence Check -> Passed -> Confidence = 0.84
- Cooldown Filter -> Failed -> Similar signal triggered within cooldown period
- Adaptive Strategy Filter -> Skipped -> Not enabled for this symbol

The implementation must make it easy to identify exactly which rule caused the signal to be rejected, blocked, ignored, or accepted.

---

## Example Decisioning Flow

A signal may pass through a flow such as:

1. Signal Detected
2. Signal Registered
3. Basic Validation
4. Market Regime Check
5. Trend Confirmation
6. Volatility Check
7. Indicator Confirmation
8. Support / Resistance Validation
9. ML Confidence Evaluation
10. Adaptive Strategy Check
11. Final Decision Made

This sequence may vary depending on the strategy design, but the system must record the actual order used at runtime.

---

## Multi-Signal Behavior

The system must support multiple signals being evaluated at the same time.

### Required Behavior

- show all active decisioning signals concurrently
- maintain a separate tracking instance for each signal
- update each signal independently
- prevent evaluation events from one signal from appearing in another signal’s timeline
- preserve ordering consistency per signal

### Important Constraint

No signal card, row, or panel should overwrite another live signal when multiple signals are active.

---

## UI / UX Requirement

The decisioning lifecycle must not be displayed as a simple final status only.

It must be shown as a **traceable live progression**.

### Accepted UI Approaches

- expandable signal cards
- vertical timeline per signal
- stepper / staged status component
- signal grid with nested rule evaluation log

### Minimum UI Outcome

The user must be able to answer the following directly from the dashboard:

- What signal is being evaluated?
- What step is it currently on?
- What rules were already checked?
- Which rules passed?
- Which rules failed?
- Which rule caused the final result?
- What was the final decision?

---

## Backend Requirement

The backend must introduce a dedicated decision tracking model for each signal.

### Required Concept

Each detected signal must have a unique decision trace keyed by a stable signal identifier.

### Required Stored Information

For each signal:

- signal ID
- symbol
- direction
- timeframe
- strategy source
- detection time
- current status
- final decision
- list of rule evaluation events in chronological order

For each rule evaluation event:

- event ID
- signal ID
- rule name
- step sequence
- status
- timestamp
- details / explanation
- optional evaluation values

### Important Technical Rule

The decision trace must be append-only during the lifecycle, so each new event is added without corrupting earlier events.

---

## API Requirement

If the dashboard uses APIs, expose the necessary data to support live decision tracking.

### Minimum API Needs

Provide access to:

- currently active signal decision traces
- per-signal rule evaluation history
- current stage and current status
- final decision when completed

### If Real-Time Transport Exists

If the system already uses SignalR, WebSocket, or another push mechanism, reuse it to push live decisioning updates to the dashboard.

If real-time infrastructure does not exist, implement a safe polling-based fallback.

---

## State Definitions

### Signal-Level States

A signal may move through states such as:

- Detected
- Evaluating
- Waiting
- DecisionMade
- Completed

### Final Decision Values

The final decision must be one of:

- Accepted
- Rejected
- Ignored
- Blocked

### Rule-Level States

Each rule evaluation must have one of:

- Passed
- Failed
- Skipped

---

## Error Handling and Consistency

The implementation must ensure the following:

- no mixed events between signals
- no missing sequence order for rule events
- no duplicated event insertion unless explicitly intended
- no UI crash if a signal finishes while being displayed
- no loss of visibility for recently completed traces
- graceful handling if ML or adaptive strategy modules are disabled

---

## Retention Behavior

Once the final decision is made, the completed decision trace should remain reviewable for a reasonable period.

This can be implemented as one of the following:

- keep recently completed traces visible in the same section for a short time
- move them into a recent decision history subsection
- allow collapse/expand while preserving trace visibility

The key requirement is that the full decision path must remain reviewable after completion and must not disappear immediately.

---

## Implementation Intent

This feature is meant to improve:

- transparency of signal decisioning
- debugging of false positives / false negatives
- understanding of why signals were accepted or rejected
- visibility into how strategy, ML, and adaptive logic interact
- troubleshooting when multiple signals are processed concurrently

---

## Short Developer Instruction

Implement a dashboard feature called **Live Signal Decision Tracking** that tracks each signal only during the decisioning phase, from **Signal Detected** until **Final Decision Made**.

Do not include order execution or trade lifecycle tracking.

For each signal, display a live step-by-step decision trace showing every evaluated rule, filter, ML check, and adaptive strategy check. Each step must show the rule name, sequence, status (Passed / Failed / Skipped), timestamp, and reason/details if available.

Each signal must end with one final decision state: **Accepted**, **Rejected**, **Ignored**, or **Blocked**.

If multiple signals are evaluated simultaneously, show and update all of them independently without overwriting or mixing their events.
