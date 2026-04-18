# UniTaskCommandBus

## Core Idea

**A command-pattern library that lets you start with a lambda and progressively expand to a class when complexity demands it — no obligation to write a command class upfront.**

Just as UnityHFSM lets you define states concisely with lambdas and then extend them by subclassing `StateBase` when needed, this library applies the same philosophy to commands.

- Simple command → define inline with a single lambda
- Complex command → extend by subclassing `CommandBase`
- Both run in the same `Invoker` in exactly the same way

## Namespace and Dependencies

All public types in this library live under the `UniTaskCommandBus` namespace.

```csharp
using UniTaskCommandBus;
```

As the name implies, **UniTask is a required dependency.** Async processing is designed around `UniTask` rather than `Task` — the performance and GC advantages in Unity make this dependency worthwhile. Users must have [UniTask](https://github.com/Cysharp/UniTask) installed before using this library.

---

## Basic Usage

### Two Invoker Types

The library provides **two Invoker types suited to different use cases.** Both are generic and share command execution, async policy management, and cancellation. The difference is **whether history (Undo/Redo) is supported.**

| Type | Use Case | API |
|---|---|---|
| `Invoker<T>` | Simple execution (fire-and-forget, input handling, etc.) | `Execute`, `ExecuteAsync`, `Cancel`, `CancelAll`, `Dispose` |
| `HistoryInvoker<T>` | Editors and tools that need Undo/Redo | All of the above + `Undo`, `Redo`, `JumpTo`, `Pop`, `Clear`, history query, `OnHistoryChanged` event |

**Why two separate types:** There is no need to pay the cost of managing an Undo stack when history is not required, and a smaller API surface is easier to work with. If all you need is "async policy + cancellation," `Invoker<T>` is cleaner.

`Invoker<T>` also has an `AsyncPolicy`, so with `Sequential` or `ThrottleLast` policies **there is an internal queue**, and `Cancel()` / `CancelAll()` behave identically to `HistoryInvoker<T>`. The only difference between the two types is the presence or absence of history.

`Invoker<T>` also provides `ExecuteAsync`, so it is fully **`await`-able.** Waiting for async work to complete or chaining operations is supported the same way. Again, the only difference is history.

```csharp
// Simple execution without history (await is still fully supported)
var runner = CommandBus.Create<CommandUnit>()
    .WithPolicy(AsyncPolicy.Sequential)
    .Build();
await runner.ExecuteAsync(saveCmd);
await runner.ExecuteAsync(uploadCmd);

// Full feature set with Undo/Redo
var editor = CommandBus.Create<SlotAssignment>()
    .WithPolicy(AsyncPolicy.Switch)
    .WithHistory(50)
    .Build();
editor.Execute(placeCmd, new SlotAssignment(3, 5));
editor.Undo();
```

> In the sections below, when "Invoker" is written alone, the common features (Execute, Cancel, etc.) apply to both types. **Undo/Redo and history-related sections are `HistoryInvoker<T>` only.**

### 1. Creating an Invoker — `CommandBus` Factory + Builder

Invokers are created through a builder pattern on the `CommandBus` factory. This is a simple creation entry point today, but routing through a factory makes it straightforward to add **Invoker registration/lookup, global cancel, debugging hooks**, and other infrastructure in the future.

#### Factory and Builder Signatures

```csharp
// Factory — entry point
public static class CommandBus
{
    public static InvokerBuilder<T> Create<T>();
}

// Builder without history
public class InvokerBuilder<T>
{
    public InvokerBuilder<T> WithPolicy(AsyncPolicy policy);
    public HistoryInvokerBuilder<T> WithHistory(int maxSize = 20);
    public Invoker<T> Build();
}

// Builder with history (after calling WithHistory)
public class HistoryInvokerBuilder<T>
{
    public HistoryInvokerBuilder<T> WithPolicy(AsyncPolicy policy);
    public HistoryInvoker<T> Build();
}
```

Calling `WithHistory` **switches the builder type itself to `HistoryInvokerBuilder<T>`.** The subsequent `.Build()` is statically fixed to return `HistoryInvoker<T>`. The contradictory combination of "configured history but received a history-less Invoker" is **prevented by the type system at compile time.**

#### Defaults

| Option | Default |
|---|---|
| `AsyncPolicy` | `Sequential` |
| `historySize` | `20` |

Options not specified in the builder use the defaults above.

#### Type Parameter `T`

`T` is the **payload type** passed when executing a command. Separating the command logic from its argument data allows a single command to be reused across multiple executions with different `T` values.

**When no argument is needed** — use the library-provided `CommandUnit` type.

```csharp
var runner = CommandBus.Create<CommandUnit>().Build();
```

**When an argument is needed** — define a **record** and pass it as `T`. Payloads are immutable data representing "what to do with which values," which maps naturally onto records' value-based equality, immutability, and concise declaration.

```csharp
public record SlotAssignment(int UnitId, int SlotIndex);

var editor = CommandBus.Create<SlotAssignment>()
    .WithHistory(30)
    .Build();
```

#### Usage Examples

```csharp
// Simple Invoker without history (all defaults)
var runner = CommandBus.Create<CommandUnit>().Build();
// → Invoker<CommandUnit>, Sequential

// Custom policy only
var burst = CommandBus.Create<CommandUnit>()
    .WithPolicy(AsyncPolicy.Drop)
    .Build();
// → Invoker<CommandUnit>, Drop

// With history (default size 20)
var editor = CommandBus.Create<SlotAssignment>()
    .WithHistory()
    .Build();
// → HistoryInvoker<SlotAssignment>, Sequential, size 20

// History + custom policy + custom size
var fullEditor = CommandBus.Create<SlotAssignment>()
    .WithPolicy(AsyncPolicy.Switch)
    .WithHistory(50)
    .Build();
// → HistoryInvoker<SlotAssignment>, Switch, size 50
```

#### Benefits of This Design

- **Type safety**: `Invoker<SlotAssignment>` only accepts `SlotAssignment`. An incorrect payload is a compile error. The return type also matches the `WithHistory` configuration automatically.
- **Command reuse**: Because command logic and argument data are separated, a single command definition can handle N executions. GC pressure is reduced.
- **Lambda-friendly**: The payload arrives as an explicit lambda argument, reducing the need to capture external variables in closures. Lambdas become more "pure" and easier to test.
- **Expressive history**: Lambda commands can build specific names with string interpolation at call time; class commands can assemble names from internal state in the `Name` property. History UIs can show "Move unit 3 to slot 5" instead of just "Move."
- **Extension entry point**: Routing through the `CommandBus` factory makes it easy to attach global configuration and Invoker management features later.

### 2. Executing a Command

Pass a command to `Execute` to run it. Commands can be defined as **lambdas** or as **classes**.

```csharp
invoker.Execute(command);
```

### 3. Command Structure

Every command defines two operations:

- **Execute**: the work the command performs
- **Undo**: the work that reverses that action

`Undo` is only actually called by `HistoryInvoker<T>`. When executed through `Invoker<T>`, a defined Undo is never invoked. Therefore **the default value for Undo is an empty lambda that does nothing.** For pure-execution use cases, Undo can be omitted. Provide it explicitly only when `HistoryInvoker<T>` needs to undo the command.

---

## Core Assumption — Unity Main Thread Only

This library **assumes it is called exclusively from the Unity main thread.** No public API is thread-safe, and behavior under concurrent calls from multiple threads is undefined.

Key conclusions that follow from this assumption:

### No External Calls During JumpTo / Undo / Redo

Because the Unity main thread is a single flow of execution, **no other `Execute`, `Undo`, or `Redo` can "interleave" while `JumpTo` is running.** While JumpTo internally iterates through multiple steps, control remains tied to the frame in which JumpTo was called; control is not returned until all steps are complete.

Similarly, it is logically impossible for an external `Execute` to arrive while `Undo` or `Redo` is executing. These re-entrant paths are not addressed by this spec.

> If you manipulate an Invoker from a coroutine or async operation, remember that those operations are also running on the main thread. Code can interleave at `await` points within async commands, but that is a different concern from the atomicity of synchronous API calls.

### Re-entry — When a Command Lambda Calls Back Into the Invoker

If `invoker.Execute(...)` is called from inside an `execute` lambda (recursive invocation), **the nested call is dequeued and executed synchronously as soon as the current call completes.**

```csharp
execute: _ =>
{
    DoSomething();
    invoker.Execute(otherCmd);  // runs immediately after the current command finishes
}
```

This prevents state corruption from re-entrant calls and guarantees a predictable execution order. Because the queue is drained continuously until empty, a single external call that triggers multiple nested calls causes all of them to be **processed sequentially within the same frame.**

**Infinite recursion is not the library's responsibility.** Logic that causes a command to call itself will blow the stack or stall the frame. No recursion depth limit or frame-spreading defense is provided; handle that yourself if needed:

```csharp
// For a command chain that might recurse indefinitely
execute: async (_, ct) =>
{
    DoSomething();
    await UniTask.Yield(ct);       // yield one frame
    invoker.Execute(nextCmd);      // no longer a recursion-depth concern
}
```

### Invoker Calls Inside Callbacks Are the User's Responsibility

Calling Invoker APIs from within an event callback such as `OnHistoryChanged` **falls outside the Invoker's control.** Event callbacks follow the default C# event system behavior; the user is responsible for ensuring safe usage (for example, using a flag to guard against re-entry or deferring to the next frame).

---

## Starting with Lambdas

The simplest form. Define a command directly as a lambda — no separate class file needed.

### Lambda Command Type Signatures

Two types are provided, for synchronous and asynchronous use.

**`Command<T>` (synchronous)**
```csharp
public class Command<T>
{
    // Basic constructor
    public Command(
        Action<T> execute,
        Action<T> undo = null,
        string name = ""
    );

    // Overload that receives the execution phase
    public Command(
        Action<T, ExecutionPhase> execute,
        Action<T> undo = null,
        string name = ""
    );
}
```

> If `undo` is passed as `null`, it is normalized internally to **an empty lambda that does nothing** — `(_ => { })`. This means Undo can be omitted without awkwardness when using `Invoker<T>` for pure execution. Provide it explicitly only when `HistoryInvoker<T>` needs to undo.

**`AsyncCommand<T>` (asynchronous)**
```csharp
public class AsyncCommand<T>
{
    // Basic constructor
    public AsyncCommand(
        Func<T, CancellationToken, UniTask> execute,
        Func<T, CancellationToken, UniTask> undo = null,
        string name = ""
    );

    // Overload that receives the execution phase
    public AsyncCommand(
        Func<T, ExecutionPhase, CancellationToken, UniTask> execute,
        Func<T, CancellationToken, UniTask> undo = null,
        string name = ""
    );
}
```

> If `undo` is passed as `null`, it is normalized internally to **an empty lambda that completes immediately** — `((_, __) => UniTask.CompletedTask)`.

Use the basic constructor when the phase is not needed; use the phase overload only when required. In either case `undo` does not receive a phase (Undo has no phase distinction). Omitting `undo` results in a no-op default.

### When No Payload Is Needed (`CommandUnit`)

When there is no payload, ignore the lambda argument with `_`.

```csharp
var invoker = CommandBus.Create<CommandUnit>().Build();

invoker.Execute(new Command<CommandUnit>(
    execute: _ => player.position += Vector3.right,
    undo:    _ => player.position -= Vector3.right
));
```

Using the shortcut method for brevity:

```csharp
invoker.Execute(
    execute: _ => player.position += Vector3.right,
    undo:    _ => player.position -= Vector3.right
);
```

### When a Payload Is Needed

The lambda receives the payload `a` as an argument; the actual value is passed at execution time.

```csharp
var invoker = CommandBus.Create<SlotAssignment>().Build();

var placeCmd = new Command<SlotAssignment>(
    execute: a => model.Assign(a.UnitId, a.SlotIndex),
    undo:    a => model.Unassign(a.SlotIndex)
);

invoker.Execute(placeCmd, new SlotAssignment(unitId: 3, slotIndex: 5));
```

> For simple operations this level of brevity is sufficient — no separate class file is required.

---

## Expanding to Classes

When command logic becomes complex, requires state, or needs to be reused, extend a base class. There are separate bases for synchronous and asynchronous commands.

- **Synchronous commands**: subclass `CommandBase<T>`
- **Asynchronous commands**: subclass `AsyncCommandBase<T>`

`T` is the same payload type as the Invoker.

### Synchronous Commands — `CommandBase<T>`

```csharp
public abstract class CommandBase<T>
{
    public abstract void Execute(T payload);
    public virtual void Execute(T payload, ExecutionPhase phase) => Execute(payload);
    public virtual void Undo(T payload) { }
    public virtual string Name => string.Empty;
}
```

> `Undo` is `virtual` with an empty default implementation. Commands intended for `Invoker<T>` only do not need to override it; override it only when `HistoryInvoker<T>` needs to undo.

Usage example:

```csharp
public class MoveCommand : CommandBase<SlotAssignment>
{
    private IModel model;

    public MoveCommand(IModel model)
    {
        this.model = model;
    }

    public override void Execute(SlotAssignment a)
    {
        model.Assign(a.UnitId, a.SlotIndex);
    }

    public override void Undo(SlotAssignment a)
    {
        model.Unassign(a.SlotIndex);
    }

    public override string Name => "Move";
}
```

When the name should vary with the payload, receive the necessary data in the constructor and assemble it in the property:

```csharp
public class MoveCommand : CommandBase<SlotAssignment>
{
    private readonly int unitId;
    private readonly int slotIndex;

    public MoveCommand(int unitId, int slotIndex)
    {
        this.unitId = unitId;
        this.slotIndex = slotIndex;
    }

    public override string Name => $"Move Unit {unitId} → Slot {slotIndex}";

    public override void Execute(SlotAssignment a) { /* ... */ }
    public override void Undo(SlotAssignment a) { /* ... */ }
}

// Create a new instance for each call
invoker.Execute(new MoveCommand(3, 5), new SlotAssignment(3, 5));
```

To branch on the phase, override the phase overload:

```csharp
public override void Execute(SlotAssignment a, ExecutionPhase phase)
{
    model.Assign(a.UnitId, a.SlotIndex);
    if (phase == ExecutionPhase.Execute)
        audio.PlayPlaceSound();  // only on first execution
}
```

### Asynchronous Commands — `AsyncCommandBase<T>`

```csharp
public abstract class AsyncCommandBase<T>
{
    public abstract UniTask ExecuteAsync(T payload, CancellationToken ct);
    public virtual UniTask ExecuteAsync(T payload, ExecutionPhase phase, CancellationToken ct)
        => ExecuteAsync(payload, ct);
    public virtual UniTask UndoAsync(T payload, CancellationToken ct) => UniTask.CompletedTask;
    public virtual string Name => string.Empty;
}
```

> `UndoAsync` is `virtual` with a default implementation that completes immediately. This mirrors the design principle of `CommandBase<T>.Undo`.

Usage example:

```csharp
public class PlaceUnitCommand : AsyncCommandBase<SlotAssignment>
{
    private IModel model;
    private IView view;

    public PlaceUnitCommand(IModel model, IView view)
    {
        this.model = model;
        this.view = view;
    }

    public override async UniTask ExecuteAsync(SlotAssignment a, CancellationToken ct)
    {
        model.Assign(a.UnitId, a.SlotIndex);
        await view.PlayPlaceAnimation(a, ct);
    }

    public override async UniTask UndoAsync(SlotAssignment a, CancellationToken ct)
    {
        model.Unassign(a.SlotIndex);
        await view.PlayRemoveAnimation(a, ct);
    }

    public override string Name => "Place Unit";
}
```

### Relationship Between the Two Bases

The synchronous and asynchronous bases are **independent types.** A single class cannot inherit from both; command authors must decide upfront whether their command is synchronous or asynchronous.

Call-site code is identical regardless of whether a lambda or class is used:

```csharp
// Synchronous command
var moveCmd = new MoveCommand(model);
invoker.Execute(moveCmd, new SlotAssignment(3, 5));

// Asynchronous command
var placeCmd = new PlaceUnitCommand(model, view);
await invoker.ExecuteAsync(placeCmd, new SlotAssignment(3, 5));
```

> Lambda commands and class commands are **treated identically by the Invoker.** You can refactor from lambda to class at any time.

---

## Asynchronous Commands (`ExecuteAsync`)

Commands may include work that takes time — animation playback, network requests, delays, and so on. Use `ExecuteAsync` for these.

### UniTask Dependency

Async processing depends on **UniTask.** UniTask is the de facto standard in Unity environments, and it offers significant advantages over `Task` in terms of GC and performance.

### Basic Usage

The lambda receives a `CancellationToken` as a second argument (in addition to the payload), and returns a `UniTask`.

```csharp
var placeCmd = new AsyncCommand<SlotAssignment>(
    execute: async (a, ct) =>
    {
        model.Assign(a.UnitId, a.SlotIndex);
        await view.PlayPlaceAnimation(a, ct);
    },
    undo: async (a, ct) =>
    {
        model.Unassign(a.SlotIndex);
        await view.PlayRemoveAnimation(a, ct);
    }
);

await invoker.ExecuteAsync(placeCmd, new SlotAssignment(3, 5));
```

### Cancellation Management — `invoker.Cancel()`

Manually creating, passing, and disposing `CancellationToken` instances for each async operation is tedious. In this library **the Invoker holds a `CancellationToken` internally**, so cancellation is a single call:

```csharp
invoker.Cancel();
```

This cancels the currently running async command. The `ct` argument in the lambda is the token the Invoker injects internally.

```csharp
// No need to create or pass a CancellationToken manually
await invoker.ExecuteAsync(placeCmd, new SlotAssignment(3, 5));

// Cancel whenever needed
invoker.Cancel();
```

Benefits of this approach:

- **Eliminates boilerplate**: No `CancellationTokenSource` creation, disposal, or token passing.
- **Clear cancellation semantics**: "Stop whatever work this Invoker has in progress" is unambiguous.
- **Pure lambdas**: The lambda just takes `(a, ct) => ...` — no need to reference an external token source.

#### `Cancel()` vs `CancelAll()`

Depending on the policy, an **internal queue** may exist. `Sequential` accumulates commands waiting to run; `ThrottleLast` holds the "last value." To flush those queues as well, use `CancelAll()`.

| Method | In-progress work | Queue |
|---|---|---|
| `Cancel()` | **Cancel all** (regardless of policy) | **Preserved** |
| `CancelAll()` | **Cancel all** (regardless of policy) | **Fully cleared** |

```csharp
// Stop only what is currently running (queue continues afterward)
invoker.Cancel();

// Stop everything in progress and clear the queue
invoker.CancelAll();
```

**Policy-independent bulk cancellation**: `Cancel()` is not affected by the `AsyncPolicy` setting. Even if multiple commands are running concurrently under `Parallel`, they are all cancelled at once. For `Sequential`, `Switch`, `Drop`, and `ThrottleLast`, whatever is currently running is cancelled without exception. The only difference between the two methods is **whether the queue is touched.**

Use `CancelAll()` when you want to **fully stop everything this Invoker is doing** — for example, on scene transition or end of an edit session. Use `Cancel()` when you only want to **stop the current in-progress work but let the queue continue.**

#### General-Purpose Cancellation — Applies to Execute, Undo, and Redo

`Cancel()` and `CancelAll()` work **regardless of the direction of execution.** Whether an Execute, Undo, or Redo is in progress, they are all cancelled the same way. Queued items are also cleared regardless of their type (Execute/Undo/Redo).

```csharp
// Cancellable in every case
await invoker.ExecuteAsync(cmd, payload);   // → cancelable with Cancel() / CancelAll()
await invoker.UndoAsync();                  // → cancelable with Cancel() / CancelAll()
await invoker.RedoAsync();                  // → cancelable with Cancel() / CancelAll()
```

The user never needs to track "which direction of work is currently running" — they just express the intent to **stop.** This meshes naturally with the design in which the cancellation token is managed at the Invoker level.

### `Execute` vs `ExecuteAsync` — Two Orthogonal Axes

In this library, "whether a command is synchronous or asynchronous" and "whether the caller wants to wait for completion" are **independent axes.** Both call styles **accept synchronous and asynchronous commands interchangeably.**

| Call style | Return type | Synchronous command | Asynchronous command |
|---|---|---|---|
| `Execute` | `ExecutionResult` | Runs immediately, returns result | **Fire-and-forget** (only starts execution; returns `Dropped`/`Cancelled`/started immediately) |
| `ExecuteAsync` | `UniTask<ExecutionResult>` | Runs immediately, returns a **completed UniTask** | Can be `await`ed until completion |

In other words:

- **`Execute`**: "Fire and forget" — does not wait for an async command to finish. The return value tells you whether the command was rejected by policy.
- **`ExecuteAsync`**: "Can wait for completion" — if a synchronous command is called this way, `await` passes through immediately.

```csharp
// Both sync and async commands — the caller chooses how to call
var r1 = invoker.Execute(syncCmd, payload);        // runs and returns (returns Completed)
var r2 = invoker.Execute(asyncCmd, payload);       // starts only, does not wait for completion

var r3 = await invoker.ExecuteAsync(syncCmd, payload);   // completes immediately, await passes through
var r4 = await invoker.ExecuteAsync(asyncCmd, payload);  // waits for animation, etc. to finish

// Ignore the return value when the result is not needed
invoker.Execute(syncCmd, payload);
await invoker.ExecuteAsync(asyncCmd, payload);
```

This means the caller does not need to know whether a command is synchronous or asynchronous, and command authors do not need to worry about how callers will invoke them.

#### Return Value Rules for `Execute` (Synchronous Call)

Because `Execute` does not wait for completion, it only determines **"did execution start?"** From this perspective, the meaning of each result value is:

| Return value | Situation |
| ----------- | --------- |
| `Completed` | Passed the policy gate — **execution has started** (for a synchronous command, the Execute call is also the completion) |
| `Dropped`   | Execution was refused by policy (`Drop` rejected because something was already running; `ThrottleLast` replaced the previous pending value with a new one) |
| `Cancelled` | **Never occurs.** At the point `Execute` returns, execution has just started, so there is no path where "cancellation is returned." |

When `Execute` is used to call an async command, execution has just started at the point of return. Whether that command is later cancelled by Switch, finishes, or throws an exception **is not communicated back to the caller** (this is the essence of fire-and-forget). If you need to know that outcome, use `ExecuteAsync` and `await`.

```csharp
// Pattern: check whether execution started
var result = invoker.Execute(saveCmd);
if (result == ExecutionResult.Dropped)
{
    Debug.Log("Already saving — skipped");
}
// Completed means "started," not "finished"
```

### `ExecuteAsync` Await Semantics — "Until This Call's Outcome Is Determined"

`ExecuteAsync` returns `UniTask<ExecutionResult>`. **The await completes at the moment the fate of that particular call is determined.** The outcome varies by policy, but the await semantics are consistent.

#### Return Type — `ExecutionResult`

```csharp
public enum ExecutionResult
{
    Completed,  // executed and finished normally
    Dropped,    // discarded by policy without being executed
    Cancelled   // cancelled while executing
}
```

**Exceptions are not expressed as results.** Exceptions thrown inside a command propagate directly to the caller. `Cancelled` represents a clean interruption by a cancellation token; in this case `OperationCanceledException` is not thrown — the result is returned as a value instead.

#### Await Behavior by Policy

| Policy | Situation | Await completes | Return value |
|---|---|---|---|
| `Sequential` | Waits in queue then executes | When **execution completes** | `Completed` |
| `Sequential` | Removed from queue by `CancelAll()` | **The moment it is removed** | `Cancelled` |
| `ThrottleLast` | Adopted as the last value and executed | When **execution completes** | `Completed` |
| `ThrottleLast` | Discarded because a newer value arrived | **The moment it is discarded** | `Dropped` |
| `ThrottleLast` | The pending "last value" removed by `CancelAll()` | **The moment it is removed** | `Cancelled` |
| `Drop` | Rejected because something was running | **The moment of rejection** | `Dropped` |
| `Switch` | Cancelled because a new Execute arrived | **The moment of cancellation** | `Cancelled` |
| `Switch` | Executes normally | When **execution completes** | `Completed` |
| `Parallel` | Starts immediately | When **execution completes** | `Completed` |
| `Parallel` | `Cancel()` called while multiple commands are running | **The moment each is cancelled** | Each `Cancelled` |
| (all) | `Cancel()` / `CancelAll()` called | **The moment of cancellation** | `Cancelled` |

The core principle: **"Did the Execute call I made finish?"** is the criterion for await. If the command waited in the queue and eventually ran to completion, you wait until then. If policy discarded or cancelled it, that is the completion moment.

**Dropped vs Cancelled distinction:**
- **`Dropped`**: the policy itself made the normal decision "do not execute" (Drop rejection, ThrottleLast middle-value replacement)
- **`Cancelled`**: interrupted by an external cancellation API (`Cancel`/`CancelAll`) or by Switch (whether running or waiting — always `Cancelled`)

A command removed from a queue by `CancelAll()` is classified as `Cancelled` because "there was an intent to execute, but external intervention stopped it."

> **Relationship with history**: A call that ends with `Dropped` never actually executed, so **it is not recorded in history.** A call that ends with `Cancelled` while still waiting to start (removed from the queue/slot) is also not recorded. A command that was already running when cancelled by Switch or `Cancel()` has already been recorded and is an Undo target. See [History Recording Rules](#history-recording-rules--only-commands-that-started-execution-are-recorded) for details.

#### Usage Example

```csharp
var result = await invoker.ExecuteAsync(saveCmd);

switch (result)
{
    case ExecutionResult.Completed:
        ShowSuccessToast();
        break;

    case ExecutionResult.Dropped:
        // A save was already in progress — this request was skipped (Drop policy)
        Debug.Log("Duplicate save request skipped");
        break;

    case ExecutionResult.Cancelled:
        // Cancelled midway — not an error, user intent
        Debug.Log("Save cancelled");
        break;
}
```

When you do not care about the result, ignore the return value. No forced try/catch.

```csharp
// Just fire-and-wait without checking the result
await invoker.ExecuteAsync(saveCmd);
```

### Async Execution Policy (`AsyncPolicy`)

Sets the behavior **when a new command arrives while an async command is executing.** This is configured at Invoker creation time. Once set, the Invoker behaves consistently according to that policy.

```csharp
var invoker = CommandBus.Create<SlotAssignment>()
    .WithPolicy(AsyncPolicy.Sequential)
    .Build();
```

#### Policy Types

| Policy | Behavior | When to use |
|---|---|---|
| **Drop** | **Discard** the new command if one is running | Prevent duplicate input (e.g., double-click prevention) |
| **Sequential** | Queue and **execute in order** | When order must be guaranteed (history, saving, etc.) |
| **Switch** | **Cancel** the current command and run the new one | When only the latest request matters (e.g., search-as-you-type) |
| **ThrottleLast** | While running, incoming commands **wait**; if more arrive, **discard the intermediate values and keep only the last**. When execution finishes, **run only the last one** | Reflecting the final state of a rapidly changing value (e.g., process only the final value of a slider drag) |
| **Parallel** | Run **concurrently without restriction** | Parallel processing of independent tasks |

#### Behavior Examples per Policy

Assume command `A` is currently executing, and `B`, `C`, `D` arrive in sequence:

- **Drop**: Only `A` executes. `B`, `C`, `D` are discarded.
- **Sequential**: `A` → `B` → `C` → `D` all execute in order.
- **Switch**: Cancel `A` → start `B` → cancel `B` → start `C` → cancel `C` → execute `D`.
- **ThrottleLast**: `B`, `C`, `D` arrive while `A` runs, but only `D` is kept. After `A` finishes, `D` executes.
- **Parallel**: `A`, `B`, `C`, `D` all execute concurrently.

> This policy only matters for **asynchronous commands.** Synchronous commands complete immediately upon invocation, so there is no "currently executing" state to speak of.

#### Undo/Redo Always Use `Switch`

Regardless of the `AsyncPolicy` setting, **`Undo` and `Redo` always behave with Switch policy.**

- When a user rapidly taps Undo/Redo, the intuitive expectation is "quickly reach the last state." Switch behavior — cancelling the in-progress revert and moving to the next one — best matches this expectation.
- If you need every Undo/Redo step to run to completion, control it yourself with `try/finally` or explicit awaiting. The default behavior is optimized for responsiveness.

```csharp
// Natural behavior when tapping rapidly: in-progress Undo is cancelled and the next Undo starts
invoker.Undo();
invoker.Undo();
invoker.Undo();

// When every step must complete, the user controls it with await
await invoker.UndoAsync();
await invoker.UndoAsync();
```

### Distinguishing Execution Phases — `ExecutionPhase`

The `execute` lambda (or class `Execute` method) is called from multiple contexts: initial execution, Redo, and plain execution through a history-less `Invoker<T>`. In most cases the behavior is the same, but there are situations — such as `catch` blocks or `finally` blocks — where you need to **branch based on which phase is currently active.**

For this, the command can receive an `ExecutionPhase` enum value as an argument.

```csharp
public enum ExecutionPhase
{
    None,     // execution through a history-less Invoker<T> (no phase concept)
    Execute,  // initial execution through HistoryInvoker<T>
    Redo      // re-execution after Undo through HistoryInvoker<T>
}
```

**Values delivered per call context:**

| Call context | `phase` value delivered |
|---|---|
| `Execute` / `ExecuteAsync` through `Invoker<T>` | `None` |
| Initial `Execute` / `ExecuteAsync` through `HistoryInvoker<T>` | `Execute` |
| `Redo` / a Redo-direction step of `JumpTo` through `HistoryInvoker<T>` | `Redo` |

#### Usage Example

```csharp
var placeCmd = new AsyncCommand<SlotAssignment>(
    execute: async (a, phase, ct) =>
    {
        try
        {
            model.Assign(a.UnitId, a.SlotIndex);
            await view.PlayPlaceAnimation(a, ct);
        }
        catch (OperationCanceledException)
        {
            // Handle cancellation during Redo differently from initial execution
            if (phase == ExecutionPhase.Redo)
                logger.Log("Redo cancelled");
            throw;
        }
        finally
        {
            // Play sound only on initial execution (skip on Redo)
            if (phase == ExecutionPhase.Execute)
                audio.PlayPlaceSound();
        }
    },
    undo: async (a, ct) => { /* ... */ }
);
```

> `Undo` is a separate lambda and needs no phase distinction. `ExecutionPhase` is a value for **distinguishing the execution context of the `execute` path.** An overload that omits the phase argument is also supported for simple commands that do not need phase information.

---

## History Management

> The content in this section is **`HistoryInvoker<T>` only.** If you only need simple execution without history, use `Invoker<T>` and skip this section.

### Structure — Pointer-Based (Photoshop Style)

History is structured as **a single list plus a current-position pointer.** This is the same approach as Photoshop's History panel.

```
[Cmd1] [Cmd2] [Cmd3] [Cmd4] [Cmd5]
                ↑
             pointer
```

- **Undo**: move the pointer **left** by one (execute the Undo of the current command)
- **Redo**: move the pointer **right** by one (execute the Execute of the next command)
- **Execute (new)**: move the pointer right and add a command. **All entries after the pointer are discarded.**

```
// Initial: [Cmd1] [Cmd2] [Cmd3] [Cmd4] [Cmd5]   pointer=5
// Undo twice
                        ↑ pointer=3

// Execute new Cmd6 here
[Cmd1] [Cmd2] [Cmd3] [Cmd6]
                       ↑ pointer=4
// Cmd4, Cmd5 are discarded (Redo no longer possible)
```

This is simpler than maintaining two separate Undo/Redo stacks, and is also more intuitive for users ("where is my current position, and how far can I go in each direction" is clear).

### History Size

The maximum history size can be specified at Invoker creation time. **The default is 20.**

```csharp
// Default (20)
var invoker = CommandBus.Create<SlotAssignment>()
    .WithHistory()
    .Build();

// Custom size
var invoker = CommandBus.Create<SlotAssignment>()
    .WithHistory(100)
    .Build();

// With policy
var invoker = CommandBus.Create<SlotAssignment>()
    .WithPolicy(AsyncPolicy.Sequential)
    .WithHistory(50)
    .Build();
```

When history exceeds the maximum size, **the oldest entry is removed first.**

#### Execute When History Is Full

When Execute is called while history is at capacity (`HistoryCount == historySize`), the following sequence occurs:

```
1. Remove the oldest entry (front of the list)
2. Record the new command in history + move the pointer
3. Fire the OnHistoryChanged(Execute, ...) event
4. Invoke the execute lambda
```

**Important**: All history manipulation is **completed before the lambda executes.** This means that if the lambda throws an exception, calling `Pop()` from the `catch` block removes exactly "the newest entry just added." The sequence — remove oldest → record → execute — is atomic and ordered, so at the point of exception handling the invariant holds: "the new command is the last entry in history."

```
Example: historySize=3, current [A][B][C] pointer=2, full

invoker.Execute(newCmd)  // exception thrown in execute

1. Remove A       → [B][C]  pointer=1
2. Record newCmd  → [B][C][newCmd]  pointer=2
3. Fire OnHistoryChanged
4. Execute runs   → exception!

invoker.Pop() in catch
→ [B][C]  pointer=1
```

`A` has already been removed and is not recovered. Users should be aware of this when executing in a full-history state where execution may fail. The same **"record → execute"** order applies in non-full situations as well, so the Pop-based exception recovery pattern behaves consistently in all cases.

**Valid range**: `historySize` must be **at least 1.** Passing 0 or a negative value throws `ArgumentOutOfRangeException` at creation time with the message "A value of at least 1 must be specified." If history size is 0, there is no reason to use `HistoryInvoker` — use `Invoker<T>` instead.

### State Change Timing — Always Change State Before Executing

Execute, Undo, and Redo all follow the same consistent three-stage pipeline: **"cancel in-progress work → change state → invoke lambda."**

```
1. Fire CancellationToken (cancel in-progress work)
2. Change history state (move pointer / record)
3. Invoke lambda
```

Undo and Redo **always use Switch policy** (see "Undo/Redo Always Use Switch" above), so they follow this three-stage pipeline without exception. Execute follows the same pipeline, but **stage 1 varies with the `AsyncPolicy` setting:**

| Policy | Stage 1 behavior for Execute |
|---|---|
| `Switch` | Cancel in-progress work (same as Undo/Redo) |
| `Drop` | Reject entry entirely if something is in progress |
| `Sequential` | Queue and wait if something is in progress |
| `ThrottleLast` | Hold as the "last value" if something is in progress |
| `Parallel` | Run in parallel without cancelling |

**But from the moment actual execution starts, stages 2 and 3 — "change state → invoke lambda" — are identical for all policies.** This means at the moment the lambda is called, the history state is always finalized.

| Operation | Stage 1 (cancel/queue handling) | Stage 2 (state change) | Stage 3 (execution) |
|---|---|---|---|
| Execute | **Depends on policy** | **Record command in history** (move pointer) | Call `execute` lambda |
| Undo | **Cancel in-progress work** (Switch) | **Move pointer left** by one | Call `undo` lambda |
| Redo | **Cancel in-progress work** (Switch) | **Move pointer right** by one | Call `execute` lambda (Redo phase) |

Benefits of this consistency:

- **Partially-executed commands are also Undo targets.** Even if an async command is cancelled mid-animation, model changes already applied can be reverted with Undo.
- **Querying `invoker.HistoryCount` etc. inside a lambda yields consistent values.** "The currently-executing command is already reflected in history" is always true.
- **After cancellation the history pointer has already moved.** If this is undesired, explicitly recover using `Pop()` or a reverse move.
- **Natural behavior when tapping Undo/Redo rapidly.** The previous animation is cancelled and the move to the next position begins immediately.

### History Recording Rules — Only Commands That Started Execution Are Recorded

The three-stage pipeline described above operates from the standpoint of **"the moment execution actually begins."** History recording only happens for commands that passed stage 1 and were decided to start executing. Commands filtered out by policy **before execution started are not recorded in history.**

Specific situations where this rule applies:

| Situation | History recorded | `ExecuteAsync` result |
|---|---|---|
| Rejected by `Drop` policy because something was running | **Not recorded** | `Dropped` |
| Discarded by `ThrottleLast` as a middle value | **Not recorded** | `Dropped` |
| Removed from `Sequential` queue by `CancelAll()` | **Not recorded** | `Cancelled` |
| `ThrottleLast`'s "last value" slot removed by `CancelAll()` | **Not recorded** | `Cancelled` |
| Waited in `Sequential` queue and started executing when its turn came | **Recorded** (at the moment execution starts) | `Completed` |
| Previously-running command cancelled by `Switch` (lambda had already started) | **Recorded** (already recorded at start time) | `Cancelled` |
| Cancelled by `Cancel()` while running in `Parallel` | **Recorded** (each one individually) | Each `Cancelled` |
| Exception thrown from inside the lambda | **Recorded** (execution had already begun) | Exception propagates |

The key criterion is **"was the `execute` lambda ever called?"** Recording happens immediately before the lambda is called, and the lambda runs immediately after. These two steps are one indivisible unit; recording cannot happen without execution, and execution cannot happen without recording.

**For Sequential, note that recording happens at "execution start time," not at the time of the `invoker.ExecuteAsync(cmd)` call.** When a call is queued, it is not yet in history. Recording happens when the item is dequeued and its turn to execute begins. This means items removed from the queue by `CancelAll()` leave no trace in history.

**Why this rule matters:**

- **Undo/Redo consistency**: Every entry in history is "a command the user has seen executed." If `Dropped` entries (never actually executed) were also recorded, there would be nothing to revert when Undo is called on them.
- **Atomicity of `Pop()`**: Because the "record → event → execute" bundle is atomic, calling `Pop()` immediately after a lambda executes removes exactly "that one command that just started." No third execution can slip between record and execute, so "execute and Pop simultaneously" never causes inconsistency.
- **Clear event ordering**: The `OnHistoryChanged(Execute, ...)` event fires **only for commands that started executing, immediately before the lambda runs.** Calls that ended with `Dropped` or `Cancelled` (before execution started) do not fire the event. The convention that "event fired = history actually changed" is maintained consistently. The reason the event fires before the lambda is to prevent the event handler from observing an incorrect state if the lambda synchronously calls `Pop()` or a re-entrant `Execute` (see [Event Timing and Exception Handling](#event-timing-and-exception-handling) for details).

> This recording rule applies equally to both `Invoker<T>` (no history) and `HistoryInvoker<T>` (with history) — it is a result of the "execution pipeline" principle common to both. `Invoker<T>` simply has no history; the "did execution start?" determination follows the same policy logic.

### Removing the Last Command — `invoker.Pop()`

There are situations where you do not want "this command to remain in history." The most typical case is **exception handling.** If an exception occurred during Execute and you want "this was a failed attempt — remove it from history," call `Pop()`.

```csharp
try
{
    await invoker.ExecuteAsync(placeCmd, new SlotAssignment(3, 5));
}
catch (InvalidOperationException)
{
    // Remove the failed command from history
    invoker.Pop();
    ShowErrorToUser();
}
```

#### Why Manual, Not Automatic

The library provides the explicit `Pop()` API rather than forcing "auto-remove on exception" because:

- **Partially-executed commands are often valid Undo targets** (safe as the default)
- "Failure = remove from history" is not always correct; it depends on the domain
- The record-first + `Pop()` combination lets **the user choose either policy**

> `Pop()` simply removes the last item from the history stack. It does not revert the effects of the `Execute` that already ran. To revert effects, explicitly call `Undo()`, or call `Undo()` before `Pop()`.

#### `Pop()` Behavior in Detail

`Pop()` removes the **physical last item** in the history list (the most recently recorded entry). However, **pointer movement and history removal only happen when the pointer is at the latest position.**

#### When the Pointer Is at the Latest Position (`CurrentIndex == HistoryCount - 1`)

When the pointer is at the end of the list (the most recent command), the sequence is: **move pointer one step left → remove last item from list → fire `OnHistoryChanged(Pop, ...)` event.**

```csharp
// Example: [Cmd1][Cmd2][Cmd3][Cmd4][Cmd5]  pointer=4 (latest position)
invoker.Pop();
// Action: pointer moves to 3 → Cmd5 removed → event fired
// Result: [Cmd1][Cmd2][Cmd3][Cmd4]  pointer=3 (over Cmd4)
```

This is `Pop()`'s primary use case: removing the most recently executed command for reasons such as exception handling. The pointer also naturally steps back to the previous position.

#### When the Pointer Is Not at the Latest Position (`CurrentIndex < HistoryCount - 1`)

When history is being browsed via Undo or JumpTo and Pop is called, **no history removal or pointer movement occurs — only `OnHistoryChanged(Pop, ...)` is fired.**

```csharp
// Example: after two Undos, [Cmd1][Cmd2][Cmd3][Cmd4][Cmd5]  pointer=2
invoker.Pop();
// Action: no history change, no pointer movement, only event fired
// Result: [Cmd1][Cmd2][Cmd3][Cmd4][Cmd5]  pointer=2 (unchanged)
```

This design exists because calling Pop while browsing history with Undo/Redo/JumpTo would otherwise cause the pointer to **shift one step back unintentionally.** Pop during browsing leaves the history untouched but fires the event so the UI can update to reflect the current state.

In this case the event's `pointerIndex` and `name` **reflect the current pointer position as-is** (since the pointer did not move).

Because Unity is single-threaded, there are no race conditions from pointer changes due to Pop. Querying `CurrentIndex` immediately after the call returns the updated value.

#### Pop Called on an Empty History

Calling `Pop()` when `HistoryCount == 0` **throws `InvalidOperationException`.** There is no target to remove, which indicates a logic error in the caller, so it is not silently treated as a no-op. (Same principle as Undo/Redo boundary exceptions.)

### Clearing All History — `invoker.Clear()`

To wipe all history and return to the initial state, call `Clear()`.

```csharp
invoker.Clear();
```

State after the call:

- `HistoryCount == 0`
- `CurrentIndex == -1`
- All previously recorded commands removed from history

#### `Clear()` Does Not Cancel

`Clear()` **only clears history.** In-progress async commands and queues are not touched.

| Method | In-progress | Queue | History |
|---|---|---|---|
| `Cancel()` | Cancelled | Preserved | Preserved |
| `CancelAll()` | Cancelled | Cleared | Preserved |
| `Clear()` | **Preserved** | **Preserved** | **Cleared** |

To do both, compose them yourself:

```csharp
// "Stop everything and clear history" — common pattern for scene transitions
invoker.CancelAll();
invoker.Clear();
```

This design follows the principle that **each method does exactly one thing.** `Pop()`, `Cancel()`, `CancelAll()`, and `Clear()` all have single responsibilities; compound behaviors are composed by the user.

### Lifecycle Termination — `invoker.Dispose()`

To fully dispose of an Invoker, call `Dispose()`. It implements `IDisposable`, so it can be used in a `using` block.

```csharp
invoker.Dispose();
```

#### Execution Order

`Dispose()` performs the following steps internally, in order:

```
1. CancelAll()             — cancel all in-progress work and clear the queue
2. Clear()                 — clear history
3. OnHistoryChanged = null — automatically unsubscribe all event listeners
4. Mark as disposed
```

In a single `Dispose()` call, "stop everything, clear history, disconnect events, and shut down" is accomplished. This integrates the `CancelAll() + Clear()` pattern shown earlier, timed to the Invoker's lifecycle end.

**Automatic event unsubscription**: `Dispose()` internally assigns `null` to the `OnHistoryChanged` event, releasing all subscribers at once. This means users do not need to manually remove handlers with `-=`, and prevents stale callbacks from firing after the Invoker is disposed.

#### Any Call After Dispose Throws `ObjectDisposedException`

After `Dispose()`, **any method call throws `ObjectDisposedException`.** It is not silently treated as a no-op.

| Call after `Dispose` | Result |
|---|---|
| `Execute` / `ExecuteAsync` | `ObjectDisposedException` |
| `Undo` / `Redo` / `JumpTo` | `ObjectDisposedException` |
| `Cancel` / `CancelAll` / `Clear` / `Pop` | `ObjectDisposedException` |
| `HistoryCount` / `CurrentIndex` / `GetName` | `ObjectDisposedException` |

The reason for this policy is the same as `JumpTo` throwing on a self-jump and `Undo` throwing on an out-of-bounds call — **accessing a disposed Invoker is almost always a lifecycle management error by the caller**, and swallowing it silently hides bugs.

```csharp
// Automatic management with a using block
using (var invoker = CommandBus.Create<SlotAssignment>().WithHistory().Build())
{
    invoker.Execute(cmd, payload);
    // Dispose() called automatically when block exits
}

// Manual management
var invoker = CommandBus.Create<SlotAssignment>().WithHistory().Build();
try
{
    invoker.Execute(cmd, payload);
}
finally
{
    invoker.Dispose();
}
```

### History Changed Event — `OnHistoryChanged`

> This event is **`HistoryInvoker<T>` only.** `Invoker<T>` has no history, so this event does not exist on it.

An event that fires whenever history state changes. Use it for UI re-rendering or displaying a "modified" indicator.

```csharp
invoker.OnHistoryChanged += (action, pointerIndex, name) =>
{
    RefreshHistoryPanel();
};
```

#### Event Signature

```csharp
event Action<HistoryActionType, int, string> OnHistoryChanged;
```

Three pieces of information are passed:

| Argument | Type | Meaning |
|---|---|---|
| `action` | `HistoryActionType` | The type of change that occurred |
| `pointerIndex` | `int` | The current pointer position after the change (same as `CurrentIndex`) |
| `name` | `string` | The name of the relevant command |

#### `HistoryActionType`

```csharp
public enum HistoryActionType
{
    Execute,  // a new command was recorded
    Undo,     // the pointer moved left
    Redo,     // the pointer moved right
    Jump,     // moved to an arbitrary position via JumpTo
    Pop,      // the last command was removed
    Clear     // all history was cleared
}
```

**Every operation** that changes history state fires the event. This includes pointer-only moves such as Undo/Redo, as well as list modifications such as Pop/Clear.

#### Meaning of the `name` Argument — Unified Rule

`name` means **the name of the command at the position the pointer has newly moved to as a result of this change.** This single rule applies consistently to all action types.

| Operation | `pointerIndex` | `name` |
|---|---|---|
| `Execute` | Index of the newly recorded command | Name of that command |
| `Undo` | Index after moving one step left | Name of the command at that position |
| `Redo` | Index after moving one step right | Name of the command at that position |
| `Jump` | Final index after jumping | Name of the command at that position |
| `Pop` (at latest position) | Index after moving one step left | Name of the command at that position |
| `Pop` (while browsing) | Current pointer position (unchanged) | Name of the command at the current position |
| `Clear` | `-1` | **Empty string** (no valid pointer position) |

From the user's perspective, the rule is consistently: **"when an event arrives, look at `name` to know 'that is the command at the currently active position.'"** Pop follows this without exception — "the command the pointer currently points to" is what appears in name (for Pop at the latest position, that is the stepped-back position; for Pop while browsing, it is the current position unchanged).

The only exception is `Clear`. After a Clear, the pointer is `-1` (empty-state marker) and there is no command to point to, so name is an empty string.

For `JumpTo`, the event does not fire for each intermediate step — it fires **exactly once, only after the final position is reached,** as a `Jump` action. Because `JumpTo` itself completes synchronously in a single call, intermediate steps are not exposed externally.

#### Event Timing and Exception Handling

`OnHistoryChanged` fires **immediately after the history state change is complete.** For Execute, **the event fires before the `execute` lambda is called** (an extension of the record-first principle).

```
Execute called
  → record in history + move pointer
  → OnHistoryChanged(Execute, ...) event fires
  → execute lambda called
  → (exception may be thrown inside the lambda)
```

This ordering is a **strict requirement.** The three steps — "record → event → lambda" — are one atomic unit that executes synchronously. No other execution path's record/event/lambda can interleave in between.

**Why the event must precede the lambda:**

The lambda is arbitrary user code that can execute synchronously on the Unity main thread. It is possible for the lambda to call `Pop()` on the same invoker, or to re-enter with another `Execute`. If the order were "lambda → event":

1. Record → lambda starts executing
2. Lambda calls `invoker.Pop()` synchronously → history state changes again
3. Lambda finishes
4. Event fires belatedly — `pointerIndex` and `name` now point to "the state after Pop." The event handler receives what looks like a notification about the just-executed command, but actually sees a completely different position.

With the "record → event → lambda" order, at the moment the event fires the lambda has not yet started, so Pop or re-entrant Execute cannot have occurred. The event's `pointerIndex`/`name` always accurately point to **the command that was just recorded.**

Therefore **even if an exception is thrown from inside the `execute` lambda, `OnHistoryChanged` has already fired.** The UI does not miss the fact that a command entered history. The user catches the exception at the `Execute`/`ExecuteAsync` call site and cleans up with `Pop()` if desired.

`Execute` and `ExecuteAsync` **do not swallow exceptions from the inner lambda — they propagate directly to the caller.** This lets the user explicitly handle exceptions (recovery, logging, Pop, etc.).

> **Calls filtered out by policy do not fire the event.** `Dropped` (Drop rejection, ThrottleLast middle-value replacement) and `Cancelled` while still pre-execution (queue/slot removed by `CancelAll()`) are not recorded in history, so `OnHistoryChanged` does not fire for them. The contract that "event fired = history actually changed" is maintained consistently. See [History Recording Rules](#history-recording-rules--only-commands-that-started-execution-are-recorded) for details.

```csharp
try
{
    await invoker.ExecuteAsync(riskyCmd, payload);
}
catch (SomeDomainException)
{
    // OnHistoryChanged has already fired at this point
    // Pop to remove this command from history if desired
    invoker.Pop();
}
```

#### Usage Example

```csharp
invoker.OnHistoryChanged += (action, index, name) =>
{
    // Update history panel
    RefreshHistoryPanel();

    // Show "modified" indicator
    if (action != HistoryActionType.Clear)
        MarkDirty();

    // Status bar message — name is "the command at the currently active position"
    statusBar.text = action switch
    {
        HistoryActionType.Execute => $"Executed: {name}",
        HistoryActionType.Undo    => $"Current position: {name} (undone)",
        HistoryActionType.Redo    => $"Current position: {name} (redone)",
        HistoryActionType.Jump    => $"Jumped to: {name}",
        HistoryActionType.Pop     => $"Current position: {name} (after removing last)",
        HistoryActionType.Clear   => "History cleared",
        _ => ""
    };
};
```

### Command Names (`name`)

Commands can be given a **name.** This name is stored along with the history entry, making it possible to **identify commands in history UI regardless of whether they were defined as lambdas or classes.**

A name is always a **plain string.** There is no library hook that automatically receives the payload value to generate a name. How the name is assembled is entirely the user's decision.

#### Lambda Commands — Constructor `name` Parameter

Lambda commands pass a `string name` to the constructor (default `""`).

```csharp
// Fixed name
var saveCmd = new Command<CommandUnit>(
    execute: _ => SaveToFile(),
    undo:    _ => RestoreFromBackup(),
    name:    "Save Document"
);

// For a dynamic name based on values at call time, assemble the string at the call site
var unitId = 3;
var slotIndex = 5;
var placeCmd = new Command<SlotAssignment>(
    execute: a => model.Assign(a.UnitId, a.SlotIndex),
    undo:    a => model.Unassign(a.SlotIndex),
    name:    $"Place Unit {unitId} at Slot {slotIndex}"
);
invoker.Execute(placeCmd, new SlotAssignment(unitId, slotIndex));
```

Lambda commands are typically created on the spot for single use, so passing a string finalized at call time is more natural than a function form that receives the payload value.

#### Class Commands — Override the `Name` Property

Class commands override the `Name` property of `CommandBase<T>` / `AsyncCommandBase<T>`. The default implementation returns `string.Empty`.

```csharp
// Fixed name
public class SaveCommand : CommandBase<CommandUnit>
{
    public override string Name => "Save Document";
    public override void Execute(CommandUnit _) { /* ... */ }
    public override void Undo(CommandUnit _) { /* ... */ }
}

// Dynamic name assembled from constructor arguments
public class MoveCommand : CommandBase<SlotAssignment>
{
    private readonly int unitId;
    private readonly int slotIndex;

    public MoveCommand(int unitId, int slotIndex)
    {
        this.unitId = unitId;
        this.slotIndex = slotIndex;
    }

    public override string Name => $"Move Unit {unitId} → Slot {slotIndex}";

    public override void Execute(SlotAssignment a) { /* ... */ }
    public override void Undo(SlotAssignment a) { /* ... */ }
}

// Usage: create a new instance per call since the name varies by payload
invoker.Execute(new MoveCommand(3, 5), new SlotAssignment(3, 5));  // "Move Unit 3 → Slot 5"
invoker.Execute(new MoveCommand(7, 2), new SlotAssignment(7, 2));  // "Move Unit 7 → Slot 2"
```

> If you want a reusable instance and the name does not need to include payload data, keep `Name` as a fixed string and call the same instance with different payloads.

#### Default — Empty String

Names are **not required.** When omitted, the default `""` (empty string) is used. Small incidental commands that do not need to appear in history can simply omit the name.

#### Finalized as a String at the Time of History Recording

When `HistoryInvoker` records a command in history, it **computes and stores the name as a final string at that moment:**

- Lambda command: the `string` passed at creation time is stored as-is.
- Class command: the `Name` property is **read once** at recording time, and that value is stored.

This means `GetName(index)` is always an O(1) string lookup, and even if external state changes or the command instance's internal fields are mutated afterward, the name already recorded in history does not change.

> Names are for **identification and display purposes.** They do not affect logic, and omitting them does not affect command behavior. They are useful for building specific history UI labels like "Undo: Place Unit 3 at Slot 5."

### History Query API

The Invoker provides the basic query API needed to build a history UI.

```csharp
// Number of commands currently in history
int count = invoker.HistoryCount;

// Current pointer position (how many commands have been applied)
int current = invoker.CurrentIndex;

// Name of the command at a specific index
string name = invoker.GetName(index);
```

These three APIs are sufficient to render a history panel (like Photoshop's History window). `CurrentIndex` is used to highlight the currently active item or to compute the target index for `JumpTo`.

```csharp
// Example: render the history panel
for (int i = 0; i < invoker.HistoryCount; i++)
{
    string label = invoker.GetName(i);
    bool isCurrent = (i == invoker.CurrentIndex);
    DrawHistoryItem(i, label, isCurrent);
}
```

#### Empty History State

When an Invoker has just been created or all commands have been removed via `Pop()` etc., the query API behaves as follows:

| API | Return value |
|---|---|
| `HistoryCount` | `0` |
| `CurrentIndex` | `-1` |

`CurrentIndex == -1` means "no command has been applied yet — initial state." Valid pointer values are in the range `0 <= CurrentIndex < HistoryCount`; `-1` is the **initial/empty-state marker** outside that range.

```csharp
var invoker = CommandBus.Create<CommandUnit>().WithHistory().Build();

invoker.HistoryCount;  // 0
invoker.CurrentIndex;  // -1

invoker.Execute(cmd);

invoker.HistoryCount;  // 1
invoker.CurrentIndex;  // 0
```

In UI code, check `CurrentIndex >= 0` to determine whether Undo is available, and `CurrentIndex < HistoryCount - 1` for Redo availability.

#### Undo/Redo Boundary Exceptions

Out-of-bounds `Undo`/`Redo` calls **throw `InvalidOperationException`.** They are not silently ignored.

| Situation | Exception |
|---|---|
| `Undo()` called when `CurrentIndex == -1` (nothing left to undo) | `InvalidOperationException` |
| `Redo()` called when `CurrentIndex == HistoryCount - 1` (nothing left to redo) | `InvalidOperationException` |

The reason is the same as `JumpTo(CurrentIndex)` throwing an exception. Out-of-bounds calls are almost always a logic error in the caller (such as forgetting to disable UI buttons), and swallowing them silently hides bugs.

```csharp
// Pattern: check button state first
undoButton.interactable = invoker.CurrentIndex >= 0;
redoButton.interactable = invoker.CurrentIndex < invoker.HistoryCount - 1;
```

> `GetName(index)` **throws `ArgumentOutOfRangeException`** when given an out-of-range index. This follows the same contract as C#'s standard `List<T>` indexer. Valid range: `0 <= index < HistoryCount`.

### Moving to an Arbitrary Position — `JumpTo(int)`

A common UX pattern in history panels is to click on any item and jump immediately to that point. `JumpTo(int)` supports this.

```csharp
invoker.JumpTo(targetIndex);
```

#### How It Works

`JumpTo` **repeats Undo or Redo in the appropriate direction** from the current pointer until it reaches the target index. Internally it repeatedly applies the basic three-stage pipeline (cancel → move → fire event → execute) for each step.

- If the target is **to the left**: repeat Undo
- If the target is **to the right**: repeat Redo

#### Synchronous Completion

**Even if the path goes through async commands, `JumpTo` completes synchronously in one call.** Each step immediately cancels the previous work via Switch policy, so intermediate animations and waits are not awaited.

This is why no separate `JumpToAsync` is needed. When the call returns, the target position has already been reached.

#### The Final Step's Async Work May Still Be Running

There is an important distinction between what is **guaranteed** and what is **not guaranteed** when `JumpTo` returns:

- ✅ **The history pointer has moved to `targetIndex`** (confirmed synchronously)
- ⚠️ **The async work (animation, etc.) of the final step's command may still be in progress**

This is consistent with the fire-and-forget philosophy of `Execute`. `JumpTo` is responsible for "moving state," not for "completing animations."

If you also want to stop the final animation, **call `Cancel()` immediately after.**

```csharp
invoker.JumpTo(targetIndex);
invoker.Cancel();  // also stop the final step's animation immediately
```

This combination achieves "confirm history state immediately and skip all visual effects." Useful for session restoration, test scenarios, and similar cases.

#### Exception Behavior

| Situation | Exception |
|---|---|
| Out-of-range index (`index < 0` or `index >= HistoryCount`) | `ArgumentOutOfRangeException` |
| Jump to current position (`index == CurrentIndex`) | `InvalidOperationException` |

A jump to the current position is an **explicit error** rather than a no-op because such a call is almost always a caller logic error (e.g., forgetting to prevent clicking the current item in the UI). Swallowing it silently hides bugs.

#### Usage Example

```csharp
// On history panel item click
void OnHistoryItemClicked(int clickedIndex)
{
    if (clickedIndex == invoker.CurrentIndex)
        return;  // ignore current item (prevent exception)

    invoker.JumpTo(clickedIndex);
    RefreshUI();
}
```

---

## Progressive Expansion Pattern

The recommended approach is to implement quickly with lambdas in the prototype phase, then naturally migrate to classes as functionality grows.

```csharp
// Step 1: Prototype — lambda, no payload
var invoker = CommandBus.Create<CommandUnit>().Build();
invoker.Execute(
    execute: _ => player.position += Vector3.right,
    undo:    _ => player.position -= Vector3.right
);

// Step 2: Data starts to vary — introduce payload
public record Move(Vector3 Delta);

var invoker = CommandBus.Create<Move>().Build();
invoker.Execute(
    new Command<Move>(
        execute: m => player.position += m.Delta,
        undo:    m => player.position -= m.Delta
    ),
    new Move(Vector3.right)
);

// Step 3: State/logic becomes complex — promote to class
invoker.Execute(new MoveCommand(player), new Move(Vector3.right));
```

---

## Out of Scope — User's Domain

This library focuses on handling **the command execution pipeline** well. The following features are intentionally not provided; implement them externally as your domain requires.

### Serialization / Persistence

There is no support for saving history to disk or restoring it across sessions.

- Using records for payloads is encouraged, so the structure is already serialization-friendly.
- Users can build external snapshots using the `HistoryCount`, `CurrentIndex`, and `GetName` query APIs.
- Actual serialization is handled far better by dedicated libraries such as MemoryPack, MessagePack, and Odin Serializer.
- If the library dictated a serialization format, it would introduce unnecessary dependencies and rigidity.

### Command Composition (Transactions)

There is no built-in support for grouping multiple commands into an atomic unit. When needed, define a "composite command" as a single command yourself. The `Pop()` and `Undo()` combination can also address some of these cases.

### Cross-Invoker Integration

There is no integrated Undo stack that combines history from multiple Invokers. Each Invoker is independent; coordinate at a higher layer if needed.

---

## Design Principles Summary

The principles this library consistently follows:

- **Progressive complexity**: A single API covers everything from a one-line lambda to class inheritance
- **Single responsibility**: Each method does exactly one thing. Compound behaviors are composed by the user (`Cancel` + `Clear`, `Pop` + `Undo`, etc.)
- **Explicit errors**: Exceptions instead of no-ops. Boundary violations, self-jumps, and disposed-object access all throw to prevent bugs from hiding
- **Orthogonal design**: Sync/async command type and Execute/ExecuteAsync call style are independent
- **Fire-and-forget friendly**: Not waiting for completion is the default; use `await` or `Cancel()` for explicit control when needed
- **Policy fixed at creation time**: Does not change at runtime, ensuring predictability
- **Library scope ends at the execution pipeline**: Serialization, persistence, and integration are the user's responsibility
