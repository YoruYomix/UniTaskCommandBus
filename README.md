# UniTaskCommandBus

A Unity command pattern library that supports lambda-first, progressive class-based extension. Built on UniTask with async policies, Undo/Redo, and history management.

## Installation

### 1. Install UniTask First (Required)

This package depends on UniTask. Install it first.

In Unity Editor: `Window > Package Manager > + > Add package from git URL...`

```
https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask
```

### 2. Install UniTaskCommandBus

After UniTask is installed, add this package using the same method:

```
https://github.com/YoruYomix/UniTaskCommandBus.git
```

## Quick Start

```csharp
using UniTaskCommandBus;

// 1. Simple lambda command (no payload)
var invoker = CommandBus.Create<CommandUnit>().Build();
invoker.Execute(execute: _ => counter++, undo: _ => counter--);

// 2. Lambda command with payload and history
var editor = CommandBus.Create<MoveArg>()
    .WithPolicy(AsyncPolicy.Sequential)
    .WithHistory(50)
    .Build();

var cmd = new Command<MoveArg>(
    execute: m => pos += m.Delta,
    undo:    m => pos -= m.Delta,
    name:    "Move"
);
editor.Execute(cmd, new MoveArg(5));
editor.Undo();
editor.Redo();
editor.JumpTo(0);

// 3. Async command with Switch policy (cancels previous on new call)
var asyncInvoker = CommandBus.Create<CommandUnit>()
    .WithPolicy(AsyncPolicy.Switch)
    .Build();

var result = await asyncInvoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
    execute: async (_, ct) => await UniTask.Delay(1000, cancellationToken: ct)
));

// 4. Class-based command with Undo
public class MoveCommand : CommandBase<int>
{
    private int _prev;
    public override string Name => "Move";
    public override void Execute(int payload) { _prev = current; current += payload; }
    public override void Undo(int payload) { current = _prev; }
}

var histInvoker = CommandBus.Create<int>().WithHistory(20).Build();
histInvoker.Execute(new MoveCommand(), 5);
histInvoker.OnHistoryChanged += (action, index, name) =>
    Debug.Log($"[{action}] index={index} name={name}");
```

## API Overview

### Builder

```csharp
CommandBus.Create<T>()
    .WithPolicy(AsyncPolicy.Sequential)   // Drop / Sequential / Switch / ThrottleLast / Parallel
    .WithHistory(maxSize: 20)             // returns HistoryInvokerBuilder
    .Build();                             // returns HistoryInvoker<T>
```

### Invoker\<T\>

| Method | Description |
|--------|-------------|
| `Execute(cmd, payload)` | Fire-and-forget execution |
| `ExecuteAsync(cmd, payload)` | Awaitable execution |
| `Cancel()` | Cancel running command; preserve queue |
| `CancelAll()` | Cancel running + drain queue |
| `Dispose()` | Cancel all, release resources |

### HistoryInvoker\<T\> (extends Invoker\<T\>)

| Method | Description |
|--------|-------------|
| `Undo()` / `UndoAsync()` | Undo current command (Switch semantics) |
| `Redo()` / `RedoAsync()` | Redo next command (Switch semantics) |
| `JumpTo(index)` | Move pointer to index via Undo/Redo; fires single Jump event |
| `Pop()` | Remove latest entry (at tip) or fire event only (browsing) |
| `Clear()` | Clear all history |
| `HistoryCount` | Number of recorded entries |
| `CurrentIndex` | Current pointer (-1 when empty) |
| `GetName(index)` | Name snapshot at index |
| `OnHistoryChanged` | `Action<HistoryActionType, int, string>` event |

### AsyncPolicy

| Policy | Behavior |
|--------|----------|
| `Drop` | Discard new commands while one is running |
| `Sequential` | Queue commands; run one at a time in order |
| `Switch` | Cancel current, start new immediately |
| `ThrottleLast` | Keep only the latest pending command |
| `Parallel` | Run all commands concurrently |

## Samples

Select this package in Package Manager to import 4 samples:

| Sample | Coverage |
|--------|----------|
| Stage 01 - Basics | Sync commands, lambda/class, CommandUnit, Name |
| Stage 02 - Async & Policies | AsyncCommand, all 5 AsyncPolicy variants, Cancel/CancelAll |
| Stage 03 - History | HistoryInvoker, Undo/Redo/Pop/Clear, OnHistoryChanged, history loading rules |
| Stage 04 - Advanced | JumpTo, Dispose, ExecutionPhase, comprehensive scenario |

Attach the `Stage0N_*Sample` component to an empty GameObject and press Play to see results in the Console.

## Documentation

See `Documentation~/UniTaskCommandBus-Spec.md` for the full API specification.

## License

MIT
