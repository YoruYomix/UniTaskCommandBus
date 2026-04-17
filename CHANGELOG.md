# Changelog

All notable changes to this project will be documented in this file.

## [0.1.0] - 2026-04-17

### Added
- `Invoker<T>` with 5 `AsyncPolicy` variants: Drop, Sequential, Switch, ThrottleLast, Parallel
- `HistoryInvoker<T>` with Undo / Redo / JumpTo / Pop / Clear and `OnHistoryChanged` event
- `Command<T>` and `AsyncCommand<T>` — lambda-based sync/async commands with optional Undo
- `CommandBase<T>` and `AsyncCommandBase<T>` — class-based sync/async commands with virtual Undo
- `CommandBus` static factory with type-safe `InvokerBuilder<T>` / `HistoryInvokerBuilder<T>`
- `CommandUnit` struct for payload-free scenarios
- `ExecutionPhase` enum (None / Execute / Redo) passed to command lambdas
- `ExecutionResult` enum (Completed / Dropped / Cancelled) returned from Execute/ExecuteAsync
- `HistoryActionType` enum (Execute / Undo / Redo / Jump / Pop / Clear) for `OnHistoryChanged`
- History loading rules: only commands that start executing are recorded; Dropped/queue-cancelled commands are not
- "Record → event → lambda" ordering guarantee for `OnHistoryChanged` on Execute
- JumpTo fires a single Jump event; intermediate steps suppress individual Undo/Redo events
- Full `IDisposable` support on both `Invoker<T>` and `HistoryInvoker<T>`
- 4 sample stages with pass/fail console output for manual testing in Unity
