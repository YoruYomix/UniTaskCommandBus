# UniTaskCommandBus

## 핵심 아이디어

**커맨드를 클래스로 만들어야 하는 부담 없이, 람다로 시작해서 복잡해지면 클래스로 점진적 확장이 가능한 커맨드 패턴 라이브러리.**

UnityHFSM이 상태(State)를 람다로 간결하게 정의하다가 필요할 때 `StateBase`를 상속한 클래스로 확장할 수 있게 해주는 것처럼, 이 라이브러리는 커맨드에도 동일한 철학을 적용한다.

- 간단한 커맨드 → 람다 한 줄로 정의
- 복잡한 커맨드 → `CommandBase`를 상속한 클래스로 확장
- 둘 다 동일한 `Invoker`에서 동일한 방식으로 실행

## 네임스페이스와 의존성

라이브러리의 모든 공개 타입은 `UniTaskCommandBus` 네임스페이스 아래에 있다.

```csharp
using UniTaskCommandBus;
```

이름에서 드러나듯 **UniTask가 필수 의존성**이다. 비동기 처리를 `Task`가 아닌 `UniTask` 기반으로 설계했기 때문에, 이 라이브러리를 쓰려면 먼저 [UniTask](https://github.com/Cysharp/UniTask)가 프로젝트에 설치되어 있어야 한다. 의존성을 이름으로 선언하여 사용자가 패키지 도입 시점에 바로 인지할 수 있도록 했다.

---

## 기본 사용법

### 두 종류의 Invoker

라이브러리는 **용도에 따라 두 가지 Invoker 타입**을 제공한다. 둘 다 제네릭이며, 커맨드 실행과 비동기 정책, 취소 관리를 공통으로 지원한다. 차이는 **히스토리(Undo/Redo) 지원 여부**다.

| 타입 | 용도 | API |
|---|---|---|
| `Invoker<T>` | 단순 실행 (fire-and-forget, 입력 처리 등) | `Execute`, `ExecuteAsync`, `Cancel`, `CancelAll`, `Dispose` |
| `HistoryInvoker<T>` | Undo/Redo가 필요한 편집기, 에디터 등 | 위의 모든 API + `Undo`, `Redo`, `JumpTo`, `Pop`, `Clear`, 히스토리 조회, `OnHistoryChanged` 이벤트 |

**별도의 타입으로 분리된 이유:** 히스토리가 필요 없는 경우까지 Undo 스택 관리 비용을 짊어질 필요가 없고, API도 작고 간단한 쪽이 사용하기 편하다. 단순히 "비동기 정책 + 취소" 용도만 원한다면 `Invoker<T>`가 깔끔하다.

`Invoker<T>`도 `AsyncPolicy`를 가지므로 `Sequential`이나 `ThrottleLast` 정책에서는 **내부에 대기열이 존재**하며, `Cancel()` / `CancelAll()`의 동작도 `HistoryInvoker<T>`와 동일하다. 두 타입의 차이는 오로지 히스토리 유무뿐이다.

`Invoker<T>`도 `ExecuteAsync`를 제공하므로 **`await` 가능하다.** 비동기 작업의 완료를 기다리거나 연속적으로 체이닝하는 것도 그대로 지원한다. 차이는 오로지 히스토리 유무뿐이다.

```csharp
// 히스토리 없이 단순 실행만 (await도 그대로 가능)
var runner = CommandBus.Create<CommandUnit>()
    .WithPolicy(AsyncPolicy.Sequential)
    .Build();
await runner.ExecuteAsync(saveCmd);
await runner.ExecuteAsync(uploadCmd);

// Undo/Redo 포함 전체 기능
var editor = CommandBus.Create<SlotAssignment>()
    .WithPolicy(AsyncPolicy.Switch)
    .WithHistory(50)
    .Build();
editor.Execute(placeCmd, new SlotAssignment(3, 5));
editor.Undo();
```

> 이후 설명에서 "Invoker"라고만 쓴 경우, 공통 기능(Execute, Cancel 등)은 양쪽 모두에 해당한다. **Undo/Redo/히스토리 관련 설명은 `HistoryInvoker<T>` 전용이다.**

### 1. Invoker 생성 — `CommandBus` 팩토리 + 빌더

Invoker는 `CommandBus` 팩토리의 빌더 패턴으로 생성한다. 지금은 단순한 생성 진입점이지만, **향후 Invoker 등록/조회, 글로벌 Cancel, 디버깅 훅** 등 인프라 기능을 추가할 수 있도록 팩토리를 거친다.

#### 팩토리와 빌더 시그니처

```csharp
// 팩토리 — 진입점
public static class CommandBus
{
    public static InvokerBuilder<T> Create<T>();
}

// 히스토리 없는 빌더
public class InvokerBuilder<T>
{
    public InvokerBuilder<T> WithPolicy(AsyncPolicy policy);
    public HistoryInvokerBuilder<T> WithHistory(int maxSize = 20);
    public Invoker<T> Build();
}

// 히스토리 있는 빌더 (WithHistory 호출 이후)
public class HistoryInvokerBuilder<T>
{
    public HistoryInvokerBuilder<T> WithPolicy(AsyncPolicy policy);
    public HistoryInvoker<T> Build();
}
```

`WithHistory`를 호출하면 **빌더 타입 자체가 `HistoryInvokerBuilder<T>`로 전환**된다. 이후의 `.Build()`는 컴파일 시점에 `HistoryInvoker<T>`를 반환하도록 고정된다. 즉 "히스토리를 설정했는데 히스토리 없는 Invoker가 반환되는" 모순 조합은 **타입 시스템이 원천 차단**한다.

#### 기본값

| 옵션 | 기본값 |
|---|---|
| `AsyncPolicy` | `Sequential` |
| `historySize` | `20` |

빌더에서 명시하지 않은 옵션은 위 기본값이 적용된다.

#### 타입 파라미터 `T`

`T`는 커맨드 실행 시 넘기는 **인자(payload) 타입**이다. 커맨드 로직과 인자(데이터)를 분리하여, 커맨드 하나를 서로 다른 `T` 값으로 여러 번 재사용할 수 있다.

**인자가 필요 없는 경우** — 라이브러리가 제공하는 `CommandUnit` 타입을 사용한다.

```csharp
var runner = CommandBus.Create<CommandUnit>().Build();
```

**인자가 필요한 경우** — **record**를 정의해서 `T`로 넘긴다. payload는 "어떤 값으로 뭘 할지"를 담는 불변 데이터이므로, record의 값 기반 비교 / 불변성 / 간결한 선언이 잘 맞는다.

```csharp
public record SlotAssignment(int UnitId, int SlotIndex);

var editor = CommandBus.Create<SlotAssignment>()
    .WithHistory(30)
    .Build();
```

#### 사용 예시

```csharp
// 히스토리 없는 단순 Invoker (모두 기본값)
var runner = CommandBus.Create<CommandUnit>().Build();
// → Invoker<CommandUnit>, Sequential

// 정책만 커스텀
var burst = CommandBus.Create<CommandUnit>()
    .WithPolicy(AsyncPolicy.Drop)
    .Build();
// → Invoker<CommandUnit>, Drop

// 히스토리 포함 (기본 크기 20)
var editor = CommandBus.Create<SlotAssignment>()
    .WithHistory()
    .Build();
// → HistoryInvoker<SlotAssignment>, Sequential, size 20

// 히스토리 + 정책 + 크기 전부 커스텀
var fullEditor = CommandBus.Create<SlotAssignment>()
    .WithPolicy(AsyncPolicy.Switch)
    .WithHistory(50)
    .Build();
// → HistoryInvoker<SlotAssignment>, Switch, size 50
```

#### 이 설계의 이점

- **타입 안전성**: `Invoker<SlotAssignment>`는 `SlotAssignment`만 받는다. 잘못된 payload는 컴파일 에러. `WithHistory` 설정과 반환 타입도 자동으로 맞춰진다.
- **커맨드 재사용**: 커맨드 정의(로직)와 인자(데이터)가 분리되어, 커맨드 하나로 N개의 실행을 처리할 수 있다. GC 부담도 줄어든다.
- **람다 친화적**: payload가 람다의 명시적 인자로 들어오니, 외부 변수를 클로저로 캡처할 필요가 줄어든다. 람다가 더 "순수"해지고 테스트도 쉬워진다.
- **히스토리 표현력**: 람다 커맨드는 호출 시점에 문자열 보간으로, 클래스 커맨드는 `Name` 프로퍼티에서 내부 상태로, 구체적인 이름을 만들 수 있다. "Move" 대신 "Move unit 3 to slot 5" 같은 구체적인 히스토리 UI가 가능하다.
- **확장의 진입점**: `CommandBus` 팩토리를 거치게 함으로써, 나중에 전역 설정, Invoker 관리 기능을 자연스럽게 붙일 수 있다.

### 2. 커맨드 실행

`Execute`에 커맨드를 넘겨 실행한다. 커맨드는 **람다**로 간단히 정의하거나, **클래스**로 정의할 수 있다.

```csharp
invoker.Execute(command);
```

### 3. 커맨드의 구성

모든 커맨드는 두 가지 동작을 정의한다:

- **Execute**: 커맨드가 수행해야 할 작업
- **Undo**: 해당 작업을 되돌리는 작업

`Undo`는 `HistoryInvoker<T>`에서만 실제로 호출된다. `Invoker<T>`로 실행할 때는 정의해두어도 사용되지 않는다. 따라서 **Undo의 기본값은 아무것도 하지 않는 빈 람다**다. 단순 실행 용도라면 Undo를 생략해도 되고, `HistoryInvoker<T>`에서 Undo가 필요하면 사용자가 오버로드 또는 오버라이드로 제공한다.

---

## 핵심 가정 — 유니티 메인 스레드 전용

이 라이브러리는 **Unity 메인 스레드에서만 호출되는 것을 가정**한다. 모든 공개 API는 스레드 안전성을 보장하지 않으며, 멀티스레드 환경에서의 동시 호출 동작은 정의되지 않는다.

이 가정에서 파생되는 중요한 결론들:

### JumpTo / Undo / Redo 실행 중 외부 호출은 불가능

유니티 메인 스레드는 단일 흐름이므로, **`JumpTo` 실행 중에 다른 `Execute`/`Undo`/`Redo`가 "끼어들" 수 없다.** JumpTo가 내부적으로 여러 스텝을 반복하는 동안 제어권은 JumpTo 호출 프레임에 묶여 있고, 모든 스텝이 끝나야 제어권이 반환된다.

마찬가지로 `Undo`/`Redo` 실행 중에 외부에서 `Execute`가 들어오는 일도 논리적으로 불가능하다. 이런 재진입 경로는 스펙에서 다루지 않는다.

> 만약 코루틴이나 비동기 작업에서 Invoker를 조작한다면, 그 작업 자체도 메인 스레드에서 돌고 있다는 점을 기억하자. 비동기 커맨드의 `await` 지점들 사이에는 다른 코드가 끼어들 수 있지만, 이는 **동기 API 호출의 원자성**과는 다른 문제다.

### 재진입 — 커맨드 람다가 Invoker를 다시 호출하면

`execute` 람다 내부에서 `invoker.Execute(...)`를 호출하는 경우(재귀 호출), **이번 호출이 완전히 끝난 후 큐에서 꺼내 동기적으로 이어서 실행**된다.

```csharp
execute: _ =>
{
    DoSomething();
    invoker.Execute(otherCmd);  // 현재 커맨드가 끝나면 바로 이어서 실행됨
}
```

이는 재진입으로 인한 상태 꼬임을 방지하고 예측 가능한 실행 순서를 보장하기 위함이다. 큐는 비워질 때까지 연속해서 처리되므로, 하나의 외부 호출이 여러 재귀 호출을 트리거하면 그 모든 호출이 **같은 프레임 안에서 순차적으로** 처리된다.

**무한 재귀는 라이브러리의 책임이 아니다.** 커맨드가 자기 자신을 호출하는 식의 로직을 짜면 스택이 터지거나 프레임이 멈춘다. 재귀 깊이 제한, 프레임 분산 같은 방어 로직은 제공하지 않으니, 필요하다면 사용자가 직접 처리해야 한다:

```csharp
// 무한 반복 가능성이 있는 커맨드 체인이라면
execute: async (_, ct) =>
{
    DoSomething();
    await UniTask.Yield(ct);       // 한 프레임 양보
    invoker.Execute(nextCmd);      // 이제 재귀 깊이 걱정 없음
}
```

### 콜백 내부의 Invoker 호출은 사용자 책임

`OnHistoryChanged` 같은 이벤트 콜백 안에서 Invoker API를 호출하는 것은 **Invoker의 통제 범위를 벗어난다.** 이벤트 콜백은 C# 이벤트 시스템의 기본 동작을 따르며, 사용자가 직접 안전하게 처리해야 한다 (예: 플래그로 재진입 방지, 다음 프레임으로 미루기 등).

---

## 람다로 시작하기

가장 간단한 형태. 클래스를 따로 만들 필요 없이 람다로 바로 정의한다.

### 람다 커맨드 타입 시그니처

동기/비동기에 따라 두 타입이 제공된다.

**`Command<T>` (동기)**
```csharp
public class Command<T>
{
    // 기본 생성자
    public Command(
        Action<T> execute,
        Action<T> undo = null,
        string name = ""
    );

    // phase를 받는 오버로드
    public Command(
        Action<T, ExecutionPhase> execute,
        Action<T> undo = null,
        string name = ""
    );
}
```

> `undo`가 `null`로 넘어오면 내부에서 **아무것도 하지 않는 빈 람다** `(_ => { })`로 정규화한다. 단순 실행 용도(`Invoker<T>`)에서는 Undo를 생략해도 어색함이 없다. `HistoryInvoker<T>`에서 Undo가 필요한 경우에만 명시적으로 제공하면 된다.

**`AsyncCommand<T>` (비동기)**
```csharp
public class AsyncCommand<T>
{
    // 기본 생성자
    public AsyncCommand(
        Func<T, CancellationToken, UniTask> execute,
        Func<T, CancellationToken, UniTask> undo = null,
        string name = ""
    );

    // phase를 받는 오버로드
    public AsyncCommand(
        Func<T, ExecutionPhase, CancellationToken, UniTask> execute,
        Func<T, CancellationToken, UniTask> undo = null,
        string name = ""
    );
}
```

> `undo`가 `null`로 넘어오면 내부에서 **아무것도 하지 않고 즉시 완료하는 빈 람다** `((_, __) => UniTask.CompletedTask)`로 정규화한다.

phase가 필요 없으면 기본 생성자를 쓰고, 필요할 때만 phase 버전을 쓴다. `undo`는 어느 쪽이든 phase를 받지 않는다 (Undo는 단계 구분이 없으므로). `undo`를 생략하면 아무것도 하지 않는 빈 동작이 기본값으로 사용된다.

### payload가 필요 없는 경우 (`CommandUnit`)

payload가 없으므로 람다 인자를 `_`로 무시한다.

```csharp
var invoker = CommandBus.Create<CommandUnit>().Build();

invoker.Execute(new Command<CommandUnit>(
    execute: _ => player.position += Vector3.right,
    undo:    _ => player.position -= Vector3.right
));
```

숏컷 메서드를 쓰면 더 간결하게:

```csharp
invoker.Execute(
    execute: _ => player.position += Vector3.right,
    undo:    _ => player.position -= Vector3.right
);
```

### payload가 있는 경우

람다가 payload `a`를 인자로 받고, 실행할 때 실제 값을 함께 넘긴다.

```csharp
var invoker = CommandBus.Create<SlotAssignment>().Build();

var placeCmd = new Command<SlotAssignment>(
    execute: a => model.Assign(a.UnitId, a.SlotIndex),
    undo:    a => model.Unassign(a.SlotIndex)
);

invoker.Execute(placeCmd, new SlotAssignment(unitId: 3, slotIndex: 5));
```

> 간단한 동작이라면 이 수준에서 끝낼 수 있다. 별도의 클래스 파일을 만들 필요가 없다.

---

## 클래스로 확장하기

커맨드의 로직이 복잡해지거나, 상태를 보관해야 하거나, 재사용이 필요해지면 베이스 클래스를 상속하여 확장한다. 동기/비동기에 따라 상속할 베이스가 나뉜다.

- **동기 커맨드**: `CommandBase<T>` 상속
- **비동기 커맨드**: `AsyncCommandBase<T>` 상속

`T`는 Invoker와 동일한 payload 타입이다.

### 동기 커맨드 — `CommandBase<T>`

```csharp
public abstract class CommandBase<T>
{
    public abstract void Execute(T payload);
    public virtual void Execute(T payload, ExecutionPhase phase) => Execute(payload);
    public virtual void Undo(T payload) { }
    public virtual string Name => string.Empty;
}
```

> `Undo`는 `virtual`이며 기본 구현은 아무것도 하지 않는다. `Invoker<T>`에서만 쓸 커맨드라면 오버라이드할 필요가 없고, `HistoryInvoker<T>`에서 Undo가 필요한 경우에만 오버라이드한다.

사용 예시:

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

payload에 따라 이름이 달라져야 한다면, 생성자에서 정보를 받아 프로퍼티에서 조립한다:

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

// 매 호출마다 인스턴스 생성
invoker.Execute(new MoveCommand(3, 5), new SlotAssignment(3, 5));
```

phase로 분기하고 싶다면 phase 오버로드를 재정의한다:

```csharp
public override void Execute(SlotAssignment a, ExecutionPhase phase)
{
    model.Assign(a.UnitId, a.SlotIndex);
    if (phase == ExecutionPhase.Execute)
        audio.PlayPlaceSound();  // 최초 실행 때만
}
```

### 비동기 커맨드 — `AsyncCommandBase<T>`

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

> `UndoAsync`는 `virtual`이며 기본 구현은 즉시 완료된다. `CommandBase<T>`의 `Undo`와 동일한 설계 원칙이다.

사용 예시:

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

### 두 베이스의 관계

동기/비동기 베이스는 **서로 독립된 타입**이다. 한 클래스가 둘 다 상속할 수는 없으며, 커맨드 작성자는 만들고자 하는 커맨드가 동기인지 비동기인지 처음에 결정해야 한다.

사용하는 쪽 코드는 람다든 클래스든 동일하다:

```csharp
// 동기 커맨드
var moveCmd = new MoveCommand(model);
invoker.Execute(moveCmd, new SlotAssignment(3, 5));

// 비동기 커맨드
var placeCmd = new PlaceUnitCommand(model, view);
await invoker.ExecuteAsync(placeCmd, new SlotAssignment(3, 5));
```

> 람다로 만든 커맨드와 클래스로 만든 커맨드는 **Invoker 입장에서 동일하게 취급된다.** 언제든 람다 → 클래스로 리팩터링할 수 있다.

---

## 비동기 커맨드 (`ExecuteAsync`)

커맨드가 애니메이션 재생, 네트워크 요청, 딜레이 등 **시간이 걸리는 작업**을 포함할 수 있다. 이때는 `ExecuteAsync`를 사용한다.

### UniTask 의존성

비동기 처리는 **UniTask**에 의존한다. 유니티 환경에서는 UniTask가 사실상 표준이고, `Task`보다 GC와 성능 측면에서 이점이 크기 때문에 이 의존성을 감수한다.

### 기본 사용법

람다가 payload에 더해 두 번째 인자로 `CancellationToken`을 받고, 반환 타입은 `UniTask`다.

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

### 취소 관리 — `invoker.Cancel()`

비동기 처리에서 취소 토큰을 매번 수동으로 생성/전달/파기하는 것은 번거롭다. 이 라이브러리에서는 **Invoker가 내부적으로 `CancellationToken`을 보관**하고, 사용자는 간단히 한 줄로 취소할 수 있다.

```csharp
invoker.Cancel();
```

이 호출은 현재 실행 중인 비동기 커맨드를 취소한다. 람다의 `ct` 인자는 Invoker가 내부에서 주입한 토큰이다.

```csharp
// 사용자가 직접 CancellationToken을 만들거나 전달할 필요 없음
await invoker.ExecuteAsync(placeCmd, new SlotAssignment(3, 5));

// 어딘가에서 취소하고 싶을 때
invoker.Cancel();
```

이 방식의 이점:

- **보일러플레이트 제거**: `CancellationTokenSource` 생성/Dispose, 토큰 전달 등의 반복 코드가 사라진다.
- **일관된 취소 의미**: "이 Invoker에서 진행 중인 작업을 멈춘다"는 의도가 명확해진다.
- **람다는 순수하게**: 람다는 `(a, ct) => ...`로 인자만 받으면 되고, 외부 토큰 소스를 참조할 필요가 없다.

#### `Cancel()` vs `CancelAll()`

정책에 따라 **대기열(큐)**이 생길 수 있다. `Sequential`은 실행 대기 중인 커맨드를 쌓아두고, `ThrottleLast`는 "마지막 값"을 보관한다. 이런 대기열까지 한꺼번에 비우려면 `CancelAll()`을 사용한다.

| 메서드 | 진행 중인 작업 | 대기열 |
|---|---|---|
| `Cancel()` | **전부 취소** (정책 무관) | **유지** |
| `CancelAll()` | **전부 취소** (정책 무관) | **전부 제거** |

```csharp
// 현재 진행 중인 것만 멈춤 (대기열은 이어서 실행됨)
invoker.Cancel();

// 진행 중 + 대기열 전부 정리 (완전히 멈춤)
invoker.CancelAll();
```

**정책과 무관한 일괄 취소**: `Cancel()`은 `AsyncPolicy` 설정에 영향받지 않는다. `Parallel` 정책에서 여러 커맨드가 동시에 돌고 있어도 모두 한꺼번에 취소되고, `Sequential`/`Switch`/`Drop`/`ThrottleLast`에서도 현재 돌고 있는 것은 예외 없이 취소된다. 두 메서드의 차이는 **"대기열을 건드릴지 말지"**일 뿐이다.

씬 전환이나 편집 세션 종료 시처럼 **"이 Invoker의 모든 작업을 완전히 끝내고 싶을 때"**는 `CancelAll()`을, **"현재 진행 중인 것만 다 날리고 대기열은 이어가고 싶을 때"**는 `Cancel()`을 사용한다.

#### 범용 취소 API — Execute / Undo / Redo 모두 적용

`Cancel()`과 `CancelAll()`은 **실행 방향에 관계없이 동작한다.** Execute 중이든, Undo 진행 중이든, Redo 진행 중이든 모두 동일하게 취소된다. 대기열에 쌓인 것도 그 종류(Execute/Undo/Redo)와 무관하게 일괄 정리된다.

```csharp
// 어떤 경우든 동일하게 취소 가능
await invoker.ExecuteAsync(cmd, payload);   // → Cancel() / CancelAll()로 취소
await invoker.UndoAsync();                  // → Cancel() / CancelAll()로 취소
await invoker.RedoAsync();                  // → Cancel() / CancelAll()로 취소
```

즉 사용자는 "지금 무슨 방향의 작업이 돌고 있는지" 신경 쓸 필요 없이, **"멈춰"라는 의도만 표현하면 된다.** 이는 취소 토큰이 Invoker 단위로 관리되는 설계와 자연스럽게 맞물린다.

### `Execute` vs `ExecuteAsync` — 직교하는 두 축

이 라이브러리에서 "커맨드가 동기인지 비동기인지"와 "호출자가 기다릴지 말지"는 **서로 독립적인 축**이다. 두 호출 방식 모두 **동기 커맨드와 비동기 커맨드를 가리지 않고 받는다.**

| 호출 방식 | 리턴 타입 | 동기 커맨드 | 비동기 커맨드 |
|---|---|---|---|
| `Execute` | `ExecutionResult` | 즉시 실행 후 결과 리턴 | **Fire-and-forget** (실행만 시작시키고 `Dropped`/`Cancelled`/실행 시작 여부만 즉시 리턴) |
| `ExecuteAsync` | `UniTask<ExecutionResult>` | 즉시 실행 후 **완료된 UniTask** 반환 | 완료될 때까지 `await` 가능 |

즉:

- **`Execute`**: "돌려놓고 신경 안 씀" — 비동기 커맨드라도 완료를 기다리지 않는다. 다만 "정책에 의해 즉시 거부/대체됐는지"는 리턴값으로 알 수 있다.
- **`ExecuteAsync`**: "완료까지 기다릴 수 있음" — 동기 커맨드를 호출해도 `await`이 그냥 즉시 통과한다.

```csharp
// 동기 커맨드든 비동기 커맨드든, 호출자는 원하는 방식으로 선택
var r1 = invoker.Execute(syncCmd, payload);        // 실행하고 끝 (Completed 반환)
var r2 = invoker.Execute(asyncCmd, payload);       // 실행 시작만, 완료는 기다리지 않음

var r3 = await invoker.ExecuteAsync(syncCmd, payload);   // 즉시 완료된 UniTask, await 바로 통과
var r4 = await invoker.ExecuteAsync(asyncCmd, payload);  // 애니메이션 등 완료까지 대기

// 결과가 필요 없으면 그냥 무시
invoker.Execute(syncCmd, payload);
await invoker.ExecuteAsync(asyncCmd, payload);
```

이 덕분에 호출자는 커맨드의 내부 구현(동기/비동기)을 몰라도 되고, 커맨드 작성자도 호출자가 어떻게 부를지 신경 쓸 필요가 없다.

#### `Execute`(동기)의 리턴값 규칙

`Execute`는 완료를 기다리지 않으므로 **"실행이 시작됐는가"만 판단**한다. 이 관점에서 각 결과값의 의미는 다음과 같다:

| 리턴값         | 상황                                                                         |
| ----------- | -------------------------------------------------------------------------- |
| `Completed` | 정책을 통과하여 **실행이 시작됨** (동기 커맨드는 익스큐트 호출이 곧 실행 완료)                            |
| `Dropped`   | 정책에 의해 실행 자체가 거부됨 (`Drop`에서 진행 중이라 거부, `ThrottleLast`에서 이전 대기값이 새 값으로 대체됨) |
| `Cancelled` | **발생하지 않음**. `Execute`는 자신의 호출이 즉시 시작되는 시점이므로 "취소되며 리턴하는 경로"가 없다.          |

`Execute`로 비동기 커맨드를 호출한 경우, 리턴 시점에는 실행이 막 시작된 상태다. 이후 해당 커맨드가 Switch로 취소되거나 완료되거나 예외를 내는 것은 **호출자에게 전달되지 않는다** (fire-and-forget의 본질). 그 운명이 궁금하다면 `ExecuteAsync`를 써서 `await`해야 한다.

```csharp
// 실행 시작 여부만 체크하는 패턴
var result = invoker.Execute(saveCmd);
if (result == ExecutionResult.Dropped)
{
    Debug.Log("이미 저장 중이라 스킵됨");
}
// Completed 여부는 "시작됐다"는 뜻일 뿐, 완료와 무관
```

### `ExecuteAsync`의 await 기준 — "이 호출의 운명이 확정될 때까지"

`ExecuteAsync`는 `UniTask<ExecutionResult>`를 반환한다. **await의 종료 시점은 "호출된 그 시점의 커맨드가 어떻게 결말지어졌는지"가 확정되는 순간**이다. 정책에 따라 결말은 달라지지만, await의 의미는 일관되다.

#### 리턴 타입 — `ExecutionResult`

```csharp
public enum ExecutionResult
{
    Completed,  // 실행되어 정상 완료됨
    Dropped,    // 정책에 의해 실행되지 않고 버려짐
    Cancelled   // 실행 중 취소됨
}
```

**예외는 결과로 표현하지 않는다.** 커맨드 내부에서 발생한 예외는 그대로 throw되어 호출자에게 전파된다. `Cancelled`는 취소 토큰에 의한 정상적인 중단을 의미하며, 이 경우에는 `OperationCanceledException`을 throw하지 않고 리턴값으로 돌려준다.

#### 정책별 await 동작

| 정책 | 상황 | await 종료 시점 | 리턴값 |
|---|---|---|---|
| `Sequential` | 큐에서 대기 후 실행 | **실행 완료**까지 대기 | `Completed` |
| `Sequential` | 큐 대기 중 `CancelAll()`로 제거됨 | **제거되는 순간** | `Cancelled` |
| `ThrottleLast` | 마지막 값으로 채택되어 실행 | **실행 완료**까지 대기 | `Completed` |
| `ThrottleLast` | 중간에 새 값이 와서 버려짐 | **버려지는 순간** | `Dropped` |
| `ThrottleLast` | 대기 중인 "마지막 값"이 `CancelAll()`로 제거됨 | **제거되는 순간** | `Cancelled` |
| `Drop` | 진행 중이라 거부됨 | **거부되는 순간** | `Dropped` |
| `Switch` | 새 Execute가 들어와서 취소됨 | **취소되는 순간** | `Cancelled` |
| `Switch` | 정상 실행됨 | **실행 완료**까지 대기 | `Completed` |
| `Parallel` | 바로 실행 | **실행 완료**까지 대기 | `Completed` |
| `Parallel` | 여러 개 동시 실행 중 `Cancel()` 호출됨 | **모든 진행 중 커맨드가 각각 취소되는 순간** | 각각 `Cancelled` |
| (공통) | `Cancel()` / `CancelAll()` 호출됨 | **취소되는 순간** | `Cancelled` |

핵심 원칙: **"내가 호출한 이 Execute가 끝났는가?"**가 await의 기준이다. 큐에서 대기하다가 결국 실행되어 끝나면 그때까지 기다리고, 정책에 의해 버려지거나 취소되면 그 순간 끝난다.

**취소 vs 드롭 구분 규칙:**
- **`Dropped`**: 정책 자체의 정상 동작으로 "실행하지 않기로 결정"된 경우 (Drop 거부, ThrottleLast 중간값 대체)
- **`Cancelled`**: 외부 취소 API(`Cancel`/`CancelAll`) 또는 Switch에 의해 중단된 경우 (실행 중이든, 대기 중이든 동일하게 `Cancelled`)

대기열에 있던 커맨드가 `CancelAll()`로 제거되는 경우는 "실행 의도가 있었으나 외부 개입으로 중단됨"이므로 `Cancelled`로 분류한다.

> **히스토리와의 관계**: `Dropped`로 끝난 호출은 실행 자체가 일어나지 않았으므로 **히스토리에 적재되지 않는다.** `Cancelled`도 "아직 실행이 시작되지 않은" 상태에서 취소된 경우(큐/슬롯 제거)는 적재되지 않는다. 반면 실행이 시작된 뒤 Switch나 `Cancel()`로 중단된 경우는 이미 적재되어 있으며 Undo 대상이 된다. 자세한 규칙은 [히스토리 적재의 기준](#히스토리-적재의-기준--실행이-시작된-커맨드만-적재된다) 참고.

#### 사용 예시

```csharp
var result = await invoker.ExecuteAsync(saveCmd);

switch (result)
{
    case ExecutionResult.Completed:
        ShowSuccessToast();
        break;

    case ExecutionResult.Dropped:
        // 이미 저장 중이라 이번 요청은 스킵됨 (Drop 정책)
        Debug.Log("저장 중복 요청 스킵");
        break;

    case ExecutionResult.Cancelled:
        // 중간에 취소됨 — 에러 아님, 사용자 의도
        Debug.Log("저장 취소됨");
        break;
}
```

실행 결과에 관심 없으면 리턴값을 무시하면 된다. try/catch를 강제로 두를 필요가 없다.

```csharp
// 결과를 체크하지 않고 fire-and-wait만
await invoker.ExecuteAsync(saveCmd);
```

### 비동기 실행 정책 (`AsyncPolicy`)

비동기 커맨드가 실행 중일 때 **새로운 커맨드가 들어오면 어떻게 할지**를 Invoker 생성 시점에 설정한다. 한 번 정하면 해당 Invoker는 그 정책에 따라 일관되게 동작한다.

```csharp
var invoker = CommandBus.Create<SlotAssignment>()
    .WithPolicy(AsyncPolicy.Sequential)
    .Build();
```

#### 정책 종류

| 정책 | 동작 | 언제 쓰나 |
|---|---|---|
| **Drop** | 실행 중이면 새 커맨드를 **버림** | 중복 입력 방지 (더블클릭 방지 등) |
| **Sequential** | 큐에 적재하고 **순차 실행** | 순서 보장이 중요한 경우 (히스토리, 저장 등) |
| **Switch** | 현재 커맨드를 **취소**하고 새 커맨드 실행 | 최신 요청만 유효한 경우 (검색어 입력 등) |
| **ThrottleLast** | 실행 중 들어온 커맨드는 **대기**, 중간에 더 들어오면 **중간값은 버리고 마지막만 유지**. 실행이 끝나면 **마지막 것만 실행** | 빠르게 변하는 값의 최종 상태 반영 (슬라이더 드래그 중 최종값만 처리 등) |
| **Parallel** | **제약 없이** 동시 실행 | 독립적인 작업들의 병렬 처리 |

#### 각 정책의 동작 예시

실행 중인 커맨드 `A`(진행 중)가 있을 때, `B` → `C` → `D`가 연달아 들어온다고 가정:

- **Drop**: `A`만 실행됨. `B`, `C`, `D`는 버려짐.
- **Sequential**: `A` → `B` → `C` → `D` 순서대로 모두 실행.
- **Switch**: `A` 취소 → `B` 시작 → `B` 취소 → `C` 시작 → `C` 취소 → `D` 실행.
- **ThrottleLast**: `A` 실행 중 `B`, `C`, `D`가 쌓이지만 `D`만 남음. `A` 끝나면 `D` 실행.
- **Parallel**: `A`, `B`, `C`, `D` 모두 동시에 실행.

> 이 정책은 **비동기 커맨드**에만 의미가 있다. 동기 커맨드는 호출 즉시 완료되므로 "실행 중" 상태 자체가 없다.

#### Undo/Redo는 항상 `Switch`

`AsyncPolicy` 설정과 무관하게, **`Undo`와 `Redo`는 항상 `Switch` 정책으로 동작한다.**

- Undo/Redo 버튼을 연타했을 때 사용자의 본능적 기대는 "빨리 마지막 상태로 이동"이다. 진행 중인 되돌리기를 취소하고 다음 것으로 넘어가는 Switch 동작이 이 기대에 가장 잘 맞는다.
- 만약 모든 Undo/Redo 스텝이 끝까지 완료되길 원한다면, 사용자가 `try/finally`나 명시적 대기로 직접 제어하면 된다. 기본 동작은 반응성에 최적화한다.

```csharp
// 연타 시 자연스러운 동작: 진행 중인 Undo는 취소되고 다음 Undo로 넘어감
invoker.Undo();
invoker.Undo();
invoker.Undo();

// 모든 스텝이 완료되길 원한다면 사용자가 await로 제어
await invoker.UndoAsync();
await invoker.UndoAsync();
```

### 실행 단계 구분 — `ExecutionPhase`

커맨드의 `execute` 람다(또는 클래스의 `Execute` 메서드)는 여러 문맥에서 호출된다: 최초 실행, Redo, 그리고 히스토리 없는 `Invoker<T>`에서의 단순 실행. 대부분의 경우 동작은 같지만, 취소 예외 처리나 `finally` 블록에서 **"지금이 어느 단계인지"에 따라 분기**해야 할 때가 있다.

이를 위해 커맨드 내부에서 `ExecutionPhase` enum 값을 인자로 받을 수 있다.

```csharp
public enum ExecutionPhase
{
    None,     // 히스토리 없는 Invoker<T>에서의 실행 (단계 개념 없음)
    Execute,  // HistoryInvoker<T>에서의 최초 실행
    Redo      // HistoryInvoker<T>에서 되돌린 후 다시 실행
}
```

**각 Invoker에서 전달되는 값:**

| 호출 상황 | 전달되는 `phase` 값 |
|---|---|
| `Invoker<T>`에서 `Execute` / `ExecuteAsync` | `None` |
| `HistoryInvoker<T>`에서 최초 `Execute` / `ExecuteAsync` | `Execute` |
| `HistoryInvoker<T>`에서 `Redo` / `JumpTo`의 Redo 방향 스텝 | `Redo` |

#### 사용 예시

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
            // Redo 중 취소와 최초 실행 중 취소를 다르게 처리
            if (phase == ExecutionPhase.Redo)
                logger.Log("Redo 취소됨");
            throw;
        }
        finally
        {
            // 최초 실행 때만 사운드 재생 (Redo에선 생략)
            if (phase == ExecutionPhase.Execute)
                audio.PlayPlaceSound();
        }
    },
    undo: async (a, ct) => { /* ... */ }
);
```

> `Undo`는 별도의 람다이므로 단계 구분이 필요 없다. `ExecutionPhase`는 **`execute` 경로의 실행 문맥을 구분**하기 위한 값이다. phase를 신경 쓸 필요 없는 단순 커맨드라면 시그니처에서 phase 인자를 생략하는 오버로드도 지원한다.

---

## 히스토리 관리

> 이 섹션의 내용은 **`HistoryInvoker<T>`** 전용이다. 히스토리가 필요 없는 단순 실행 용도라면 `Invoker<T>`를 사용하고 이 섹션은 건너뛰어도 된다.

### 구조 — 포인터 방식 (포토샵 스타일)

히스토리는 **하나의 리스트 + 현재 위치 포인터**로 구성된다. 포토샵의 히스토리 패널과 동일한 방식이다.

```
[Cmd1] [Cmd2] [Cmd3] [Cmd4] [Cmd5]
                ↑
              포인터
```

- **Undo**: 포인터를 **왼쪽**으로 한 칸 이동 (현재 커맨드의 Undo 실행)
- **Redo**: 포인터를 **오른쪽**으로 한 칸 이동 (다음 커맨드의 Execute 실행)
- **Execute (신규)**: 포인터를 오른쪽으로 이동하면서 커맨드 추가. **포인터 이후 항목은 모두 버려진다.**

```
// 초기: [Cmd1] [Cmd2] [Cmd3] [Cmd4] [Cmd5]   포인터=5
// Undo 두 번 실행
                        ↑ 포인터=3

// 여기서 새 Cmd6 Execute
[Cmd1] [Cmd2] [Cmd3] [Cmd6]
                       ↑ 포인터=4
// Cmd4, Cmd5는 버려짐 (Redo 불가)
```

이 방식은 Undo/Redo 스택 두 개를 따로 유지하는 것보다 단순하고, 사용자에게도 직관적이다 ("내 현재 위치는 어디이고, 앞뒤로 얼마나 갈 수 있는가"가 명확).

### 히스토리 크기 설정

Invoker 생성 시 히스토리 최대 크기를 지정할 수 있다. **기본값은 20.**

```csharp
// 기본값 (20)
var invoker = CommandBus.Create<SlotAssignment>()
    .WithHistory()
    .Build();

// 커스텀 크기
var invoker = CommandBus.Create<SlotAssignment>()
    .WithHistory(100)
    .Build();

// 정책과 함께
var invoker = CommandBus.Create<SlotAssignment>()
    .WithPolicy(AsyncPolicy.Sequential)
    .WithHistory(50)
    .Build();
```

히스토리가 최대 크기를 초과하면 **가장 오래된 항목부터 제거**된다.

#### 꽉 찬 상태에서 Execute가 호출되면

히스토리가 꽉 찬 상태(`HistoryCount == historySize`)에서 새 Execute가 들어오면 다음 순서로 처리된다:

```
1. 가장 오래된 항목(리스트 맨 앞) 제거
2. 새 커맨드를 히스토리에 적재 + 포인터 이동
3. OnHistoryChanged(Execute, ...) 이벤트 발생
4. execute 람다 호출
```

**중요**: 모든 히스토리 조작은 **람다 실행 전에 완료**된다. 이 덕분에 람다에서 예외가 throw되더라도 사용자가 `catch` 블록에서 `Pop()`을 호출하면 "방금 추가한 최신 항목"만 정확히 빠진다. 오래된 항목 제거 → 적재 → 실행이 원자적으로 순서대로 일어나므로, 예외 대응 시점에서는 "히스토리에는 새 커맨드가 마지막에 들어가 있는 상태"라는 불변식이 보장된다.

```
예: historySize=3, 현재 [A][B][C] 포인터=2, 꽉 찬 상태

invoker.Execute(newCmd)  // execute에서 예외 발생

1. A 제거        → [B][C]  포인터=1
2. newCmd 적재   → [B][C][newCmd]  포인터=2
3. OnHistoryChanged 발생
4. execute 실행 → 예외!

catch에서 invoker.Pop()
→ [B][C]  포인터=1
```

A는 이미 제거됐으므로 복구되지 않는다. 이 점은 꽉 찬 상태에서 Execute가 실패할 가능성이 있는 경우 사용자가 인지해야 한다. 꽉 차지 않은 일반 상황도 동일하게 **"적재 → 실행"** 순서이므로 Pop으로의 예외 복구 패턴은 어느 상황에서나 동일하게 동작한다.

**유효 범위**: `historySize`는 **1 이상**이어야 한다. 0 이하의 값을 넘기면 생성 시 `ArgumentOutOfRangeException`을 던지며, 예외 메시지로 "최소 1 이상의 값을 지정해야 합니다"를 안내한다. 히스토리가 0이라면 `HistoryInvoker`를 쓸 이유가 없으므로 `Invoker<T>`로 대체하라는 신호다.

### 상태 변경 시점 — 항상 선(先) 변경, 후(後) 실행

Execute, Undo, Redo 모두 **"진행 중인 작업 취소 → 상태 변경 → 람다 실행"**이라는 일관된 3단계 파이프라인을 따른다.

```
1. CancellationToken 발동 (진행 중인 작업 취소)
2. 히스토리 상태 변경 (포인터 이동 / 적재)
3. 람다 실행
```

Undo와 Redo는 **항상 Switch 정책**이므로 (위의 "Undo/Redo는 항상 `Switch`" 참고) 언제나 이 3단계를 그대로 따른다. Execute는 `AsyncPolicy` 설정에 따라 **1단계의 동작이 달라진다:**

| 정책 | Execute의 1단계 동작 |
|---|---|
| `Switch` | 진행 중 작업을 취소 (Undo/Redo와 동일) |
| `Drop` | 진행 중이면 진입 자체를 거부 |
| `Sequential` | 진행 중이면 큐에 적재하고 대기 |
| `ThrottleLast` | 진행 중이면 "마지막 값"으로 보관 |
| `Parallel` | 취소하지 않고 병렬 실행 |

**하지만 실제 실행이 시작되는 순간부터는 모든 정책에서 `2. 상태 변경 → 3. 람다 실행` 순서가 동일하다.** 즉 람다가 호출되는 시점에는 항상 히스토리 상태가 최종 형태로 확정되어 있다.

| 작업 | 1단계 (취소/대기 처리) | 2단계 (상태 변경) | 3단계 (실행) |
|---|---|---|---|
| Execute | **정책에 따름** | 커맨드를 **히스토리에 적재** (포인터 이동) | `execute` 람다 호출 |
| Undo | **진행 중 작업 취소** (Switch) | **포인터를 왼쪽으로** 한 칸 이동 | `undo` 람다 호출 |
| Redo | **진행 중 작업 취소** (Switch) | **포인터를 오른쪽으로** 한 칸 이동 | `execute` 람다 호출 (Redo phase) |

이 일관성이 주는 이점:

- **부분 실행된 커맨드도 Undo 대상**이 된다. 비동기 커맨드가 애니메이션 도중 취소되더라도, 이미 모델에 반영된 변경은 Undo로 되돌릴 수 있다.
- **람다 내부에서 `invoker.HistoryCount` 등을 조회할 때 일관된 값**이 나온다. "지금 실행 중인 커맨드는 이미 히스토리에 반영되어 있다"가 항상 참이다.
- **취소가 발생해도 히스토리 포인터는 이미 움직여 있다.** 사용자가 원치 않으면 `Pop()`이나 역방향 이동으로 명시적으로 복구한다.
- **Undo/Redo 연타 시 자연스러운 동작.** 이전 애니메이션이 취소되고 바로 다음 지점으로 이동이 시작된다.

### 히스토리 적재의 기준 — "실행이 시작된 커맨드만 적재된다"

앞 섹션의 3단계 파이프라인은 **"실행이 실제로 시작되는 순간"**을 기준으로 작동한다. 즉 히스토리 적재는 정책의 1단계를 통과해서 실행이 시작되기로 결정된 커맨드에 한해 일어난다. 정책 판단에서 걸러져 **실행이 시작되지 않은 커맨드는 히스토리에도 적재되지 않는다.**

이 규칙이 적용되는 구체적 상황:

| 상황 | 히스토리 적재 | `ExecuteAsync` 결과 |
|---|---|---|
| `Drop` 정책에서 진행 중이라 거부됨 | **적재 안 됨** | `Dropped` |
| `ThrottleLast`에서 중간값으로 덮어써져 버려짐 | **적재 안 됨** | `Dropped` |
| `Sequential` 큐에서 대기 중 `CancelAll()`로 제거됨 | **적재 안 됨** | `Cancelled` |
| `ThrottleLast`의 "마지막 값" 슬롯이 `CancelAll()`로 제거됨 | **적재 안 됨** | `Cancelled` |
| `Sequential` 큐에서 대기하다가 자기 차례가 와서 실행됨 | **적재됨** (실행 시작 시점에) | `Completed` |
| `Switch`로 취소된 이전 커맨드 (이미 람다가 돌기 시작함) | **적재됨** (Undo 대상) | `Cancelled` |
| `Parallel`로 동시 실행 중 `Cancel()`로 중단 | **적재됨** (각각) | 각각 `Cancelled` |
| 람다 내부에서 예외 throw | **적재됨** (이미 실행에 들어갔으므로) | 예외 그대로 전파 |

핵심 기준은 **"`execute` 람다가 한 번이라도 호출되기 시작했는가"**이다. 호출 시작 직전에 적재가 일어나고, 그 직후에 람다가 돈다. 이 두 스텝은 한 묶음이며, 적재 없이 실행되거나 실행 없이 적재되는 경우는 없다.

**Sequential의 경우 적재 시점이 호출 시점이 아니라 "실행 시작 시점"**이라는 점에 주의한다. `invoker.ExecuteAsync(cmd)`가 호출되어 큐에 쌓였을 때는 아직 히스토리에 안 들어가 있고, 큐에서 꺼내져 자기 차례가 와서 실행이 시작되는 그 시점에 적재된다. 이 덕분에 큐에서 `CancelAll()`로 제거된 대기 항목은 히스토리에 흔적을 남기지 않는다.

**이 규칙이 중요한 이유:**

- **Undo/Redo의 일관성**: 히스토리에 있는 항목은 모두 "사용자가 보기에 실행된 적이 있는 작업"이다. 실행된 적 없는 `Dropped` 항목까지 히스토리에 남으면 Undo로 되돌릴 실체가 없다.
- **`Pop()`의 원자성**: "적재 → 이벤트 → 실행" 묶음이 원자적이므로, 람다 직후에 `Pop()`을 호출하면 "방금 시작된 그 커맨드 하나"만 정확히 빠진다. 적재와 실행 사이에 제3의 실행 항목이 끼어들 여지가 없어서, "실행과 동시에 Pop"해도 불일치가 생기지 않는다.
- **이벤트 순서의 명확성**: `OnHistoryChanged(Execute, ...)` 이벤트는 실행이 시작된 커맨드에 대해서만, **람다 실행 직전에** 발생한다. `Dropped`/`Cancelled`(미실행)로 끝난 호출에는 이벤트가 발생하지 않는다. 람다보다 이벤트를 먼저 행하는 이유는 람다 내부에서 동기 `Pop()`이나 재진입 `Execute`가 일어나도 이벤트 핸들러가 엉뚱한 상태를 보지 않도록 하기 위함이다 (자세한 내용은 [이벤트 발생 시점과 예외 처리](#이벤트-발생-시점과-예외-처리) 참고).

> 이 적재 규칙은 `Invoker<T>`(히스토리 없음)와 `HistoryInvoker<T>`(히스토리 있음) 모두에 동일하게 적용되는 "실행 파이프라인" 원칙의 결과이다. `Invoker<T>`에는 히스토리가 없을 뿐, "실행 시작 여부" 판단 자체는 똑같이 정책을 따른다.

### 마지막 커맨드 제거 — `invoker.Pop()`

때로는 "이 커맨드를 히스토리에 남기고 싶지 않다"는 상황이 있다. 대표적인 경우가 **예외 처리**다. Execute 도중 예외가 발생했고, 사용자 입장에서 "이건 실패한 시도이므로 히스토리에서 빼고 싶다"면 `Pop()`을 호출하면 된다.

```csharp
try
{
    await invoker.ExecuteAsync(placeCmd, new SlotAssignment(3, 5));
}
catch (InvalidOperationException)
{
    // 실패한 커맨드를 히스토리에서 제거
    invoker.Pop();
    ShowErrorToUser();
}
```

#### 왜 자동이 아니라 수동인가

라이브러리가 "예외 시 자동 제거"를 강제하지 않고 `Pop()`이라는 명시적 API를 제공하는 이유:

- **부분 실행된 커맨드도 Undo 대상**이 되어야 하는 경우가 많다 (기본값으로 안전)
- "실패 = 히스토리에서 제거"가 항상 옳은 것은 아니다. 사용자의 도메인에 따라 다르다.
- 선적재 + `Pop()` 조합으로 **두 정책 모두 사용자가 선택 가능**해진다.

> `Pop()`은 단순히 히스토리 스택의 마지막 항목을 제거할 뿐이다. 이미 실행된 `Execute`의 효과를 되돌리지는 않는다. 되돌리려면 명시적으로 `Undo()`를 호출하거나, `Pop()` 전에 `Undo()`를 먼저 실행해야 한다.

#### `Pop()`의 동작 상세

`Pop()`은 **히스토리 리스트의 물리적 마지막 항목**(가장 최근에 적재된 것)을 제거한다. 단, **포인터 이동과 히스토리 제거는 포인터가 최신 위치에 있을 때만** 수행된다.

#### 포인터가 최신 위치에 있을 때 (`CurrentIndex == HistoryCount - 1`)

포인터가 리스트의 맨 끝(최신 커맨드)을 가리키고 있으면, **포인터 한 칸 왼쪽 이동 → 리스트 끝 항목 제거 → `OnHistoryChanged(Pop, ...)` 이벤트 발행** 순서로 동작한다.

```csharp
// 예: [Cmd1][Cmd2][Cmd3][Cmd4][Cmd5]  포인터=4 (최신 위치)
invoker.Pop();
// 동작: 포인터 3으로 이동 → Cmd5 제거 → 이벤트 발행
// 결과: [Cmd1][Cmd2][Cmd3][Cmd4]  포인터=3 (Cmd4 위)
```

이 경우가 `Pop()`의 주된 사용 시나리오다. 방금 Execute한 커맨드를 예외 처리 등의 이유로 제거하는 패턴이며, 포인터도 자연스럽게 이전 위치로 물러난다.

#### 포인터가 최신 위치가 아닐 때 (`CurrentIndex < HistoryCount - 1`)

Undo나 JumpTo로 히스토리를 탐색 중일 때 Pop이 호출되면, **히스토리 제거와 포인터 이동 없이 `OnHistoryChanged(Pop, ...)` 이벤트만 발행**한다.

```csharp
// 예: Undo를 두 번 해서 [Cmd1][Cmd2][Cmd3][Cmd4][Cmd5]  포인터=2 인 상태
invoker.Pop();
// 동작: 히스토리 변경 없음, 포인터 이동 없음, 이벤트만 발행
// 결과: [Cmd1][Cmd2][Cmd3][Cmd4][Cmd5]  포인터=2 (변화 없음)
```

이렇게 설계한 이유: Undo/Redo/JumpTo로 히스토리를 탐색하는 중에 Pop이 호출되면, 의도와 다르게 포인터가 한 칸 뒤로 밀리는 **잘못된 동작**이 발생할 수 있다. 탐색 중의 Pop은 히스토리를 건드리지 않되, 이벤트는 발행하여 UI가 현재 상태에 맞게 갱신될 수 있도록 한다.

이 경우 이벤트의 `pointerIndex`와 `name`은 **현재 포인터 위치 그대로**를 반영한다 (포인터가 이동하지 않았으므로).

유니티는 단일 스레드이므로 Pop으로 인한 포인터 변경에 레이스 컨디션은 없다. 호출 직후 `CurrentIndex`를 조회하면 업데이트된 값이 바로 반영되어 있다.

#### 빈 히스토리에서 Pop 호출

`HistoryCount == 0`인 상태에서 `Pop()`을 호출하면 **`InvalidOperationException`을 던진다.** 제거할 대상이 없다는 것은 호출자의 로직 오류이므로 조용히 no-op 처리하지 않는다. (Undo/Redo 경계 예외와 동일한 원칙)

### 히스토리 전체 비우기 — `invoker.Clear()`

히스토리를 통째로 비우고 초기 상태로 되돌리려면 `Clear()`를 호출한다.

```csharp
invoker.Clear();
```

호출 후 상태:

- `HistoryCount == 0`
- `CurrentIndex == -1`
- 이전에 적재된 모든 커맨드가 히스토리에서 제거됨

#### `Clear()`는 취소를 하지 않는다

`Clear()`는 **히스토리만 비운다.** 진행 중인 비동기 커맨드나 대기열은 건드리지 않는다.

| 메서드 | 진행 중 | 대기열 | 히스토리 |
|---|---|---|---|
| `Cancel()` | 취소 | 유지 | 유지 |
| `CancelAll()` | 취소 | 제거 | 유지 |
| `Clear()` | **유지** | **유지** | **제거** |

둘 다 하고 싶다면 사용자가 직접 조합한다:

```csharp
// "모든 걸 멈추고 히스토리도 비운다" = 씬 전환 시 자주 쓰는 패턴
invoker.CancelAll();
invoker.Clear();
```

이 설계는 **각 메서드가 하나의 일만 한다**는 원칙을 따른다. `Pop()`, `Cancel()`, `CancelAll()`, `Clear()` 모두 단일 책임을 가지고, 복합 동작은 사용자가 조합하는 방식이다.

### 생명주기 종료 — `invoker.Dispose()`

Invoker를 완전히 폐기하려면 `Dispose()`를 호출한다. `IDisposable`을 구현하므로 `using` 블록에서도 쓸 수 있다.

```csharp
invoker.Dispose();
```

#### 동작 순서

`Dispose()`는 내부적으로 다음을 순서대로 수행한다:

```
1. CancelAll()           — 진행 중인 작업과 대기열 전부 정리
2. Clear()               — 히스토리 비우기
3. OnHistoryChanged = null  — 모든 이벤트 구독자 자동 해제
4. Dispose 상태로 표시
```

즉 `Dispose()` 하나로 "모든 걸 멈추고 히스토리도 비우고 이벤트도 끊고 끝낸다"가 이루어진다. 앞서 본 `CancelAll() + Clear()` 조합을 Invoker 생명주기 종료 시점에 맞춰 통합한 것이다.

**이벤트 자동 해제**: `Dispose()`는 내부적으로 `OnHistoryChanged` 이벤트에 `null`을 대입하여 모든 구독자를 일괄 해제한다. 이 덕분에 사용자가 핸들러를 일일이 `-=`로 해제할 필요가 없고, Invoker 폐기 후 잔여 콜백이 호출되는 사고를 막는다.

#### 이후 호출은 `ObjectDisposedException`

`Dispose()` 이후에 **어떤 메서드든 호출하면 `ObjectDisposedException`을 던진다.** 조용히 no-op 처리하지 않는다.

| `Dispose` 이후 호출 | 결과 |
|---|---|
| `Execute` / `ExecuteAsync` | `ObjectDisposedException` |
| `Undo` / `Redo` / `JumpTo` | `ObjectDisposedException` |
| `Cancel` / `CancelAll` / `Clear` / `Pop` | `ObjectDisposedException` |
| `HistoryCount` / `CurrentIndex` / `GetName` | `ObjectDisposedException` |

이 정책의 이유는 `JumpTo`가 자기자리 점프를, `Undo`가 경계 밖 호출을 예외로 던지는 것과 동일하다 — **이미 폐기된 Invoker에 접근하는 것은 거의 항상 호출자의 생명주기 관리 오류**이므로 조용히 넘어가면 버그를 숨기게 된다.

```csharp
// using 블록으로 자동 관리
using (var invoker = CommandBus.Create<SlotAssignment>().WithHistory().Build())
{
    invoker.Execute(cmd, payload);
    // 블록 종료 시 자동으로 Dispose() 호출
}

// 수동 관리
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

### 히스토리 변경 이벤트 — `OnHistoryChanged`

> 이 이벤트는 **`HistoryInvoker<T>` 전용**이다. `Invoker<T>`에는 히스토리 자체가 없으므로 이 이벤트도 존재하지 않는다.

히스토리 상태가 바뀔 때마다 발생하는 이벤트다. UI 리렌더링이나 "수정됨" 표시 등에 사용한다.

```csharp
invoker.OnHistoryChanged += (action, pointerIndex, name) =>
{
    RefreshHistoryPanel();
};
```

#### 이벤트 시그니처

```csharp
event Action<HistoryActionType, int, string> OnHistoryChanged;
```

튜플로 세 가지 정보를 넘긴다:

| 인자 | 타입 | 의미 |
|---|---|---|
| `action` | `HistoryActionType` | 발생한 동작의 종류 |
| `pointerIndex` | `int` | 변경 후 현재 포인터 위치 (`CurrentIndex`와 동일) |
| `name` | `string` | 관련된 커맨드의 이름 |

#### `HistoryActionType`

```csharp
public enum HistoryActionType
{
    Execute,  // 새 커맨드가 적재됨
    Undo,     // 포인터가 왼쪽으로 이동
    Redo,     // 포인터가 오른쪽으로 이동
    Jump,     // JumpTo로 임의 위치 이동
    Pop,      // 마지막 커맨드 제거
    Clear     // 히스토리 전체 비우기
}
```

히스토리 상태를 변경하는 **모든 동작**이 이벤트를 발생시킨다. Undo/Redo처럼 포인터만 이동하는 경우도, Pop/Clear처럼 리스트를 수정하는 경우도 포함된다.

#### `name` 인자의 의미 — 통일 규칙

`name`은 **이 변경으로 인해 포인터가 새로 가리키게 된 위치의 커맨드 이름**을 의미한다. 모든 action에 동일하게 적용되는 단일 규칙이다.

| 동작 | `pointerIndex` | `name` |
|---|---|---|
| `Execute` | 적재된 새 커맨드의 인덱스 | 그 커맨드의 이름 |
| `Undo` | 한 칸 왼쪽으로 이동한 인덱스 | 그 위치 커맨드의 이름 |
| `Redo` | 한 칸 오른쪽으로 이동한 인덱스 | 그 위치 커맨드의 이름 |
| `Jump` | 점프한 최종 인덱스 | 그 위치 커맨드의 이름 |
| `Pop` (최신 위치) | 한 칸 왼쪽으로 이동한 인덱스 | 그 위치 커맨드의 이름 |
| `Pop` (탐색 중) | 현재 포인터 위치 (변화 없음) | 현재 포인터 위치 커맨드의 이름 |
| `Clear` | `-1` | **빈 문자열** (유효한 포인터 위치 없음) |

즉 사용자 입장에서는 **"이벤트가 오면 `name`을 보고 '지금 활성 위치의 커맨드가 이거구나'"**라고 일관되게 해석할 수 있다. Pop도 예외 없이 "현재 포인터가 가리키는 커맨드"가 name에 담긴다 (최신 위치에서의 Pop은 뒤로 물러난 위치, 탐색 중 Pop은 현재 위치 그대로).

예외는 `Clear`뿐이다. Clear 후에는 포인터가 `-1`(빈 상태 표식)이 되어 가리킬 커맨드가 없으므로 name은 빈 문자열이다.

`JumpTo`의 경우 중간 스텝마다 이벤트가 발생하지 않고, **최종 지점 도달 후 한 번만** `Jump` 이벤트가 발생한다. `JumpTo` 자체가 동기적으로 한 번에 완료되는 API이므로, 중간 단계는 외부에 노출하지 않는다.

#### 생 시점과 예외 처리

`OnHistoryChanged`는 **히스토리 상태 변경이 완료된 직후 즉시** 발생한다. Execute의 경우 **`execute` 람다가 호출되기 전에 이벤트가 먼저 발생**한다 (선적재 원칙의 연장선).

```
Execute 호출
  → 히스토리 적재 + 포인터 이동
  → OnHistoryChanged(Execute, ...) 이벤트 발생
  → execute 람다 호출
  → (람다 내부에서 예외 발생 가능)
```

이 순서는 **엄격한 요구사항**이며, "적재 → 이벤트 → 람다" 세 스텝은 한 묶음으로 원자적으로 일어난다. 중간에 다른 실행 경로의 적재/이벤트/람다가 끼어들 수 없다.

**왜 이벤트가 람다보다 먼저여야 하는가:**

람다는 유니티 메인 스레드에서 동기적으로 실행될 수 있는 임의의 사용자 코드다. 람다 안에서 같은 invoker의 `Pop()`을 호출하거나, 재진입으로 또 다른 `Execute`를 호출하는 일이 가능하다. 만약 "람다 → 이벤트" 순서였다면:

1. 적재 → 람다 실행 시작
2. 람다 내부에서 `invoker.Pop()` 동기 호출 → 히스토리 상태가 다시 바뀜
3. 람다 종료
4. 뒤늦게 이벤트 발행 — 이때 `pointerIndex`와 `name`은 "방금 Pop된 뒤의 상태"를 가리키고 있음. 이벤트 핸들러는 "방금 실행된 커맨드"에 대한 통지로 받았는데 실제로는 전혀 다른 위치를 본다.

"적재 → 이벤트 → 람다" 순서에서는 이벤트가 발행되는 그 순간 아직 람다가 시작도 안 했으므로 Pop이나 재진입 Execute가 일어날 수 없고, 이벤트의 `pointerIndex`/`name`은 항상 **"방금 적재된 바로 그 커맨드"**를 정확히 가리킨다.

따라서 **`execute` 람다 내부에서 예외가 throw되더라도, `OnHistoryChanged`는 이미 발생한 상태다.** 이 덕분에 UI는 "커맨드가 히스토리에 들어갔다"는 사실을 놓치지 않는다. 사용자는 `Execute`/`ExecuteAsync` 호출 쪽에서 try/catch로 예외를 받아 필요시 `Pop()`으로 정리하면 된다.

`Execute`와 `ExecuteAsync`는 **내부 람다의 예외를 삼키지 않고 호출자에게 그대로 throw**한다. 이는 사용자가 예외 대응(복구, 로그, Pop 등)을 명시적으로 처리할 수 있게 하기 위함이다.

> **정책으로 걸러진 호출은 이벤트도 발생시키지 않는다.** `Dropped`(Drop 거부, ThrottleLast 중간값 대체)나 "아직 실행 시작 전 상태에서 취소된 `Cancelled`"(큐/슬롯에서 `CancelAll()`로 제거)는 히스토리에 적재되지 않으므로 `OnHistoryChanged`도 발생하지 않는다. 이벤트 발생 = 히스토리가 실제로 변했다는 신호라는 규약이 일관되게 유지된다. 자세한 규칙은 [히스토리 적재의 기준](#히스토리-적재의-기준--실행이-시작된-커맨드만-적재된다) 참고.

```csharp
try
{
    await invoker.ExecuteAsync(riskyCmd, payload);
}
catch (SomeDomainException)
{
    // OnHistoryChanged는 이미 발생한 상태
    // 이 커맨드를 히스토리에 남기고 싶지 않다면 Pop
    invoker.Pop();
}
```

#### 사용 예시

```csharp
invoker.OnHistoryChanged += (action, index, name) =>
{
    // 히스토리 패널 업데이트
    RefreshHistoryPanel();

    // "수정됨" 표시
    if (action != HistoryActionType.Clear)
        MarkDirty();

    // 상태 표시줄 메시지 — name은 "현재 활성 위치의 커맨드"
    statusBar.text = action switch
    {
        HistoryActionType.Execute => $"실행됨: {name}",
        HistoryActionType.Undo    => $"현재 위치: {name} (되돌림)",
        HistoryActionType.Redo    => $"현재 위치: {name} (다시 실행)",
        HistoryActionType.Jump    => $"이동: {name}",
        HistoryActionType.Pop     => $"현재 위치: {name} (마지막 제거 후)",
        HistoryActionType.Clear   => "히스토리 초기화",
        _ => ""
    };
};
```

### 커맨드 이름 (`name`)

커맨드에 **이름**을 붙일 수 있다. 이 이름은 히스토리에 함께 저장되어, **람다로 만든 커맨드든 클래스로 만든 커맨드든 히스토리 UI에서 식별**할 수 있게 해준다.

이름은 어느 쪽이든 **단순 문자열**이다. payload 값을 자동으로 받아 이름을 만들어주는 라이브러리 훅은 없다. 이름을 어떻게 조립할지는 전적으로 사용자가 결정한다.

#### 람다 커맨드 — 생성자 `name` 파라미터

람다 커맨드는 생성자에 `string name` 을 넘긴다 (기본값 `""`).

```csharp
// 고정 이름
var saveCmd = new Command<CommandUnit>(
    execute: _ => SaveToFile(),
    undo:    _ => RestoreFromBackup(),
    name:    "Save Document"
);

// 호출 시점의 값으로 동적 이름이 필요하면, 호출부에서 문자열을 조립해서 넘김
var unitId = 3;
var slotIndex = 5;
var placeCmd = new Command<SlotAssignment>(
    execute: a => model.Assign(a.UnitId, a.SlotIndex),
    undo:    a => model.Unassign(a.SlotIndex),
    name:    $"Place Unit {unitId} at Slot {slotIndex}"
);
invoker.Execute(placeCmd, new SlotAssignment(unitId, slotIndex));
```

람다 커맨드는 대개 "그 자리에서 일회성으로 생성"되므로, payload 값을 받는 함수 형태보다 호출 시점에 확정된 문자열을 넘기는 편이 자연스럽다.

#### 클래스 커맨드 — `Name` 프로퍼티 오버라이드

클래스 커맨드는 `CommandBase<T>` / `AsyncCommandBase<T>`의 `Name` 프로퍼티를 오버라이드한다. 기본 구현은 `string.Empty`이다.

```csharp
// 고정 이름
public class SaveCommand : CommandBase<CommandUnit>
{
    public override string Name => "Save Document";
    public override void Execute(CommandUnit _) { /* ... */ }
    public override void Undo(CommandUnit _) { /* ... */ }
}

// 생성자로 받은 값을 조립한 동적 이름
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

// 사용: 이름이 payload별로 달라지므로 매 호출마다 인스턴스 생성
invoker.Execute(new MoveCommand(3, 5), new SlotAssignment(3, 5));  // "Move Unit 3 → Slot 5"
invoker.Execute(new MoveCommand(7, 2), new SlotAssignment(7, 2));  // "Move Unit 7 → Slot 2"
```

> 재사용 가능한 인스턴스를 원하고 이름에 payload가 개입하지 않아도 된다면, `Name` 을 고정 문자열로 두고 인스턴스 하나를 여러 payload로 호출하면 된다.

#### 기본값 — 빈 문자열

이름은 **필수가 아니다.** 지정하지 않으면 기본값 `""`(빈 문자열)이 사용된다. 히스토리에 남기고 싶지 않은 자잘한 커맨드는 그냥 생략하면 된다.

#### 히스토리 적재 시점에 문자열로 확정된다

`HistoryInvoker`는 커맨드가 히스토리에 적재되는 시점에 이름을 **최종 문자열로 계산해서 저장**한다:

- 람다 커맨드: 생성 시 넘긴 `string` 이 그대로 저장됨.
- 클래스 커맨드: 적재 시점에 `Name` 프로퍼티를 **한 번** 읽고, 그 값이 저장됨.

즉 히스토리의 `GetName(index)` 는 항상 O(1) 문자열 조회이며, 나중에 외부 상태가 변하거나 커맨드 인스턴스의 내부 필드가 바뀌어도 이미 적재된 이름은 바뀌지 않는다.

> 이름은 **식별 및 표시용**이다. 로직에는 영향을 주지 않으며, 남기지 않아도 커맨드 동작에는 문제가 없다. 히스토리 UI에서 "Place Unit 3 at Slot 5 되돌리기" 같은 구체적인 표시에 활용할 수 있다.

### 히스토리 조회 API

Invoker는 히스토리 UI를 구성하는 데 필요한 기본적인 조회 API를 제공한다.

```csharp
// 현재 히스토리에 쌓인 커맨드 개수
int count = invoker.HistoryCount;

// 현재 포인터 위치 (어떤 커맨드까지 적용된 상태인지)
int current = invoker.CurrentIndex;

// 특정 인덱스의 커맨드 이름 조회
string name = invoker.GetName(index);
```

이 세 API만 있으면 히스토리 패널(포토샵의 History 윈도우 같은) UI를 충분히 그릴 수 있다. `CurrentIndex`는 현재 활성 항목을 하이라이트하거나 `JumpTo` 대상 인덱스를 계산하는 데 사용한다.

```csharp
// 히스토리 패널 렌더링 예시
for (int i = 0; i < invoker.HistoryCount; i++)
{
    string label = invoker.GetName(i);
    bool isCurrent = (i == invoker.CurrentIndex);
    DrawHistoryItem(i, label, isCurrent);
}
```

#### 빈 히스토리 상태

Invoker를 갓 생성했거나 모든 커맨드를 `Pop()` 등으로 제거한 경우, 조회 API는 다음과 같이 동작한다:

| API | 반환값 |
|---|---|
| `HistoryCount` | `0` |
| `CurrentIndex` | `-1` |

`CurrentIndex`가 `-1`인 것은 "어떤 커맨드도 아직 적용되지 않은 초기 상태"를 의미한다. 유효한 포인터 값은 `0 <= CurrentIndex < HistoryCount` 범위이며, `-1`은 이 범위 밖의 **초기/빈 상태 표식**이다.

```csharp
var invoker = CommandBus.Create<CommandUnit>().WithHistory().Build();

invoker.HistoryCount;  // 0
invoker.CurrentIndex;  // -1

invoker.Execute(cmd);

invoker.HistoryCount;  // 1
invoker.CurrentIndex;  // 0
```

UI에서 "Undo 가능 여부"를 체크할 때는 `CurrentIndex >= 0`으로, "Redo 가능 여부"는 `CurrentIndex < HistoryCount - 1`로 판단할 수 있다.

#### Undo/Redo 경계 예외

범위를 벗어난 `Undo`/`Redo` 호출은 **`InvalidOperationException`**을 던진다. 조용히 무시되지 않는다.

| 상황 | 예외 |
|---|---|
| `CurrentIndex == -1`에서 `Undo()` 호출 (더 이상 되돌릴 게 없음) | `InvalidOperationException` |
| `CurrentIndex == HistoryCount - 1`에서 `Redo()` 호출 (더 이상 다시 할 게 없음) | `InvalidOperationException` |

이유는 `JumpTo(CurrentIndex)`가 예외를 던지는 것과 동일하다. 경계 상태에서의 호출은 거의 항상 호출자의 로직 오류(UI 버튼 비활성화 누락 등)이므로, 조용히 no-op 처리하면 버그를 숨기게 된다.

```csharp
// UI 버튼 활성화 상태를 먼저 체크하는 패턴
undoButton.interactable = invoker.CurrentIndex >= 0;
redoButton.interactable = invoker.CurrentIndex < invoker.HistoryCount - 1;
```

> 이름은 커맨드 생성 시 지정한 `name` 함수에 payload를 적용하여 생성된 문자열이다. 이름을 지정하지 않은 커맨드는 빈 문자열이 반환된다.

> `GetName(index)`는 범위를 벗어난 인덱스를 넘기면 **`ArgumentOutOfRangeException`을 던진다.** C# 표준 리스트(`List<T>`)의 인덱서와 동일한 규약을 따른다. 유효 범위는 `0 <= index < HistoryCount`.

### 임의 위치로 이동 — `JumpTo(int)`

히스토리 패널에서 특정 항목을 클릭하면 그 지점으로 즉시 이동하는 UX가 일반적이다. 이를 위해 `JumpTo(int)`를 제공한다.

```csharp
invoker.JumpTo(targetIndex);
```

#### 동작 방식

`JumpTo`는 현재 포인터에서 목표 인덱스까지 **방향에 맞게 Undo 또는 Redo를 반복**하며 한 번에 이동한다. 내부적으로는 기본 3단계 파이프라인(취소 → 이동 → 이벤트 발행 → 실행)을 스텝마다 반복 수행한다.

- 목표가 **왼쪽**에 있으면: Undo를 반복
- 목표가 **오른쪽**에 있으면: Redo를 반복

#### 동기 처리

**비동기 커맨드를 거쳐 이동하더라도 `JumpTo`는 동기적으로 한 번에 완료된다.** Switch 정책에 따라 각 스텝은 이전 작업을 즉시 취소하면서 진행하기 때문에, 중간 단계의 애니메이션이나 대기를 기다리지 않는다.

이 덕분에 별도의 `JumpToAsync`는 제공하지 않는다. 호출 후 제어권이 반환될 때는 이미 목표 지점에 도달해 있다.

#### 최종 지점의 비동기 작업은 계속 진행 중

`JumpTo`가 반환되는 시점에 **보장되는 것**과 **보장되지 않는 것**을 구분해야 한다:

- ✅ **히스토리 포인터는 `targetIndex`로 이동 완료** (동기적으로 확정)
- ⚠️ **최종 지점 커맨드의 비동기 작업(애니메이션 등)은 아직 실행 중일 수 있음**

이는 `Execute`의 fire-and-forget 철학과 동일하다. `JumpTo`는 "상태 이동"을 책임지고, "애니메이션 완료"는 책임지지 않는다.

최종 애니메이션도 원치 않는다면 **직후에 `Cancel()`을 호출**하면 된다.

```csharp
invoker.JumpTo(targetIndex);
invoker.Cancel();  // 최종 지점의 애니메이션까지 즉시 중단
```

이 조합으로 "히스토리 상태만 즉시 확정하고 시각 효과는 전부 생략"이 가능하다. 세션 복원, 테스트 시나리오 등에 유용하다.

#### 예외 동작

| 상황 | 예외 |
|---|---|
| 범위를 벗어난 인덱스 (`index < 0` 또는 `index >= HistoryCount`) | `ArgumentOutOfRangeException` |
| 현재 위치로 점프 (`index == CurrentIndex`) | `InvalidOperationException` |

자기 자리로의 점프를 no-op으로 처리하지 않고 **명시적 에러**로 던지는 이유는, 그런 호출은 거의 항상 호출자의 로직 오류(예: UI에서 현재 항목 클릭 방지 누락)이기 때문이다. 조용히 넘어가면 버그를 숨기게 된다.

#### 사용 예시

```csharp
// 히스토리 패널에서 특정 항목 클릭 시
void OnHistoryItemClicked(int clickedIndex)
{
    if (clickedIndex == invoker.CurrentIndex)
        return;  // 현재 항목이면 무시 (예외 방지)

    invoker.JumpTo(clickedIndex);
    RefreshUI();
}
```

---

## 점진적 확장 패턴

프로토타입 단계에서는 람다로 빠르게 구현하고, 기능이 늘어나면 자연스럽게 클래스로 옮겨가는 흐름을 권장한다.

```csharp
// Step 1: 프로토타입 — 람다, payload 없음
var invoker = CommandBus.Create<CommandUnit>().Build();
invoker.Execute(
    execute: _ => player.position += Vector3.right,
    undo:    _ => player.position -= Vector3.right
);

// Step 2: 데이터가 달라지기 시작 — payload 도입
public record Move(Vector3 Delta);

var invoker = CommandBus.Create<Move>().Build();
invoker.Execute(
    new Command<Move>(
        execute: m => player.position += m.Delta,
        undo:    m => player.position -= m.Delta
    ),
    new Move(Vector3.right)
);

// Step 3: 상태/로직 복잡 — 클래스로 승격
invoker.Execute(new MoveCommand(player), new Move(Vector3.right));
```

---

## 범위 밖 — 사용자 영역

이 라이브러리는 **커맨드 실행 파이프라인**을 잘 다루는 것에 집중한다. 다음 기능들은 의도적으로 제공하지 않으며, 사용자가 필요에 맞게 외부에서 처리한다.

### 직렬화 / 영속화

히스토리를 디스크에 저장하거나 세션 간 복원하는 기능은 제공하지 않는다.

- payload를 record로 쓰는 것을 권장하므로 직렬화 친화적인 구조는 이미 갖춰져 있다.
- `HistoryCount`, `CurrentIndex`, `GetName` 등의 조회 API로 사용자가 외부에서 스냅샷을 만들 수 있다.
- 실제 직렬화는 MemoryPack, MessagePack, Odin Serializer 등 **전문 라이브러리가 훨씬 잘 처리**한다.
- 라이브러리가 직렬화 포맷을 정하면 불필요한 의존성과 경직성이 생긴다.

### 커맨드 합성 (트랜잭션)

여러 커맨드를 원자적 단위로 묶는 기능은 제공하지 않는다. 필요한 경우 사용자가 "합성 커맨드"를 하나의 커맨드로 직접 정의하면 된다. `Pop()`과 `Undo()` 조합으로도 어느 정도 대응 가능하다.

### Invoker 간 통합

여러 Invoker의 히스토리를 하나로 묶는 통합 Undo 스택은 제공하지 않는다. 각 Invoker는 독립적이며, 필요한 경우 상위 레이어에서 조율하면 된다.

---

## 설계 원칙 요약

이 라이브러리가 일관되게 따르는 원칙들:

- **점진적 복잡도**: 람다 한 줄에서 클래스 상속까지, 같은 API로 커버
- **단일 책임**: 각 메서드는 하나의 일만. 복합 동작은 사용자가 조합 (`Cancel` + `Clear`, `Pop` + `Undo` 등)
- **명시적 에러**: no-op 대신 예외. 경계 위반, 자기자리 점프, Disposed 접근 모두 예외로 던져 버그 숨김 방지
- **직교 설계**: 동기/비동기 커맨드와 Execute/ExecuteAsync 호출 방식이 독립적
- **Fire-and-forget 친화**: 완료를 기다리지 않는 것이 기본. 필요하면 `await` 또는 `Cancel()`로 명시적 제어
- **정책은 생성 시점에 고정**: 런타임에 바뀌지 않아 예측 가능성 확보
- **라이브러리의 범위는 실행 파이프라인까지**: 직렬화, 영속화, 통합 등은 사용자 몫
