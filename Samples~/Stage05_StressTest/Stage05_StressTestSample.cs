using System;
using System.Collections.Generic;
using UnityEngine;
using Cysharp.Threading.Tasks;
using UniTaskCommandBus;
using Debug = UnityEngine.Debug;

namespace UniTaskCommandBus.Samples
{
    /// <summary>
    /// Stage 05 — High-intensity mixed API stress tests.
    /// Each test combines multiple features to catch integration bugs.
    /// </summary>
    public class Stage05_StressTestSample : MonoBehaviour
    {
        private int _passed;
        private int _failed;

        private void Pass(string label)
        {
            _passed++;
            Debug.Log($"[Stage05]   ✅ {label} 통과");
        }

        private void Fail(string label, string detail)
        {
            _failed++;
            Debug.Log($"[Stage05]   ❌ [실패] {label}: {detail}");
        }

        private void Start() => RunAsync().Forget();

        private async UniTaskVoid RunAsync()
        {
            // ── 테스트 1: Switch + History — Undo 진행 중 Execute → Undo 취소 + 분기 ──
            // 흐름: A 완료 → B 완료 → Undo B 시작(200ms) → 30ms 후 C Execute
            // 기대: Undo B(currentIndex=0)에서 C 실행 → B 폐기, C가 index 1로 기록
            //        HistoryCount=2, names=[A,C], Undo=Cancelled
            Debug.Log("[Stage05] ▶ 테스트 1: Switch + Undo 진행 중 Execute → Undo 취소 + 분기");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Switch)
                    .WithHistory(10)
                    .Build();

                int val = 0;
                // A·B 순서대로 완료
                await invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                    execute: async (_, ct) => { await UniTask.Delay(30, cancellationToken: ct); val = 1; },
                    undo: async (_, ct) => { await UniTask.Delay(200, cancellationToken: ct); val = 0; },
                    name: "A"), CommandUnit.Default);

                await invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                    execute: async (_, ct) => { await UniTask.Delay(30, cancellationToken: ct); val = 2; },
                    undo: async (_, ct) => { await UniTask.Delay(200, cancellationToken: ct); val = 1; },
                    name: "B"), CommandUnit.Default);
                // [A,B] currentIndex=1, val=2

                // Undo B 시작 (200ms) — currentIndex=0 로 이동
                var undoTask = invoker.UndoAsync();
                await UniTask.Delay(30); // Undo 진행 중

                // C Execute → Switch로 Undo 취소, currentIndex=0에서 분기: B 폐기, C 기록
                await invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                    execute: async (_, ct) => { await UniTask.Delay(30, cancellationToken: ct); val = 3; },
                    name: "C"), CommandUnit.Default);

                var undoResult = await undoTask;

                bool ok = invoker.HistoryCount == 2
                          && invoker.CurrentIndex == 1
                          && invoker.GetName(0) == "A"
                          && invoker.GetName(1) == "C"
                          && undoResult == ExecutionResult.Cancelled
                          && val == 3;

                if (ok) Pass("Switch + Undo 중 Execute: [A,C] HistoryCount=2, Undo=Cancelled, val=3");
                else    Fail("Switch + Undo 중 Execute",
                             $"count={invoker.HistoryCount} idx={invoker.CurrentIndex} " +
                             $"names=[{(invoker.HistoryCount>0?invoker.GetName(0):"?")},{(invoker.HistoryCount>1?invoker.GetName(1):"?")}] " +
                             $"undo={undoResult} val={val}");

                invoker.Dispose();
            }

            // ── 테스트 2: Sequential + History — 큐 순서와 히스토리 적재 순서 일치 ──
            // 5개의 비동기 커맨드를 Sequential로 실행. 모두 완료 후 히스토리 순서가 실행 순서와 같아야 한다.
            Debug.Log("[Stage05] ▶ 테스트 2: Sequential + History — 큐 순서 = 히스토리 순서");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Sequential)
                    .WithHistory(10)
                    .Build();

                var order = new List<int>();
                var tasks = new List<UniTask<ExecutionResult>>();

                for (int i = 0; i < 5; i++)
                {
                    int captured = i;
                    tasks.Add(invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                        execute: async (_, ct) =>
                        {
                            await UniTask.Delay(30, cancellationToken: ct);
                            order.Add(captured);
                        },
                        name: $"Seq#{captured}"), CommandUnit.Default));
                }

                await UniTask.WhenAll(tasks);

                bool orderOk = order.Count == 5;
                for (int i = 0; i < 5 && orderOk; i++)
                    orderOk = order[i] == i;

                bool historyOk = invoker.HistoryCount == 5;
                for (int i = 0; i < 5 && historyOk; i++)
                    historyOk = invoker.GetName(i) == $"Seq#{i}";

                if (orderOk && historyOk)
                    Pass("Sequential 실행 순서와 히스토리 순서 일치");
                else
                    Fail("Sequential 순서", $"order=[{string.Join(",", order)}] histOk={historyOk}");

                invoker.Dispose();
            }

            // ── 테스트 3: ThrottleLast + History — 중간값 히스토리 미기록 ──
            // A 실행 중: B, C, D 연속 Execute → B·C Dropped, D만 실행
            // 최종 HistoryCount=2 (A, D), B·C는 없음
            Debug.Log("[Stage05] ▶ 테스트 3: ThrottleLast + History — 중간값 히스토리 미기록");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.ThrottleLast)
                    .WithHistory(10)
                    .Build();

                int eventCount = 0;
                invoker.OnHistoryChanged += (_, __, ___) => eventCount++;

                var taskA = invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                    execute: async (_, ct) => await UniTask.Delay(150, cancellationToken: ct),
                    name: "A"), CommandUnit.Default);

                await UniTask.Delay(20); // A 실행 중

                var taskB = invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(execute: async (_, ct) => await UniTask.Delay(50, cancellationToken: ct), name: "B"), CommandUnit.Default);
                var taskC = invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(execute: async (_, ct) => await UniTask.Delay(50, cancellationToken: ct), name: "C"), CommandUnit.Default);
                var taskD = invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(execute: async (_, ct) => await UniTask.Delay(50, cancellationToken: ct), name: "D"), CommandUnit.Default);

                var results = await UniTask.WhenAll(taskA, taskB, taskC, taskD);

                bool ok = results.Item1 == ExecutionResult.Completed
                          && results.Item2 == ExecutionResult.Dropped
                          && results.Item3 == ExecutionResult.Dropped
                          && results.Item4 == ExecutionResult.Completed
                          && invoker.HistoryCount == 2
                          && invoker.GetName(0) == "A"
                          && invoker.GetName(1) == "D"
                          && eventCount == 2; // A, D만 OnHistoryChanged

                if (ok) Pass("ThrottleLast: A·D만 히스토리 기록, 이벤트 2회");
                else    Fail("ThrottleLast 히스토리",
                             $"results=({results.Item1},{results.Item2},{results.Item3},{results.Item4}) " +
                             $"count={invoker.HistoryCount} events={eventCount}");

                invoker.Dispose();
            }

            // ── 테스트 4: CancelAll — 큐 대기 항목 히스토리 미기록 ──
            // Sequential: A 실행 시작 → B·C 큐 대기 → CancelAll
            // 기대: A는 Cancelled(적재됨), B·C는 Cancelled(미적재)
            Debug.Log("[Stage05] ▶ 테스트 4: Sequential + CancelAll — 큐 대기 항목 히스토리 미기록");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Sequential)
                    .WithHistory(10)
                    .Build();

                var taskA = invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                    execute: async (_, ct) => await UniTask.Delay(500, cancellationToken: ct),
                    name: "A"), CommandUnit.Default);
                var taskB = invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                    execute: async (_, ct) => await UniTask.Delay(50, cancellationToken: ct),
                    name: "B"), CommandUnit.Default);
                var taskC = invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                    execute: async (_, ct) => await UniTask.Delay(50, cancellationToken: ct),
                    name: "C"), CommandUnit.Default);

                await UniTask.Delay(50);
                invoker.CancelAll();

                var results = await UniTask.WhenAll(taskA, taskB, taskC);

                bool ok = results.Item1 == ExecutionResult.Cancelled
                          && results.Item2 == ExecutionResult.Cancelled
                          && results.Item3 == ExecutionResult.Cancelled
                          && invoker.HistoryCount == 1  // A만 적재 (실행 시작됨)
                          && invoker.GetName(0) == "A";

                if (ok) Pass("CancelAll: A만 히스토리 기록, B·C 미기록");
                else    Fail("CancelAll 히스토리",
                             $"results=({results.Item1},{results.Item2},{results.Item3}) " +
                             $"histCount={invoker.HistoryCount}");

                invoker.Dispose();
            }

            // ── 테스트 5: JumpTo + 새 Execute → 미래 항목 폐기 + 새 분기 ──
            // 5개 실행 → JumpTo(1) → 새 Execute("New") → 인덱스 2·3·4 폐기, New가 index 2
            Debug.Log("[Stage05] ▶ 테스트 5: JumpTo 후 새 Execute → 미래 폐기 + 새 분기");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Switch)
                    .WithHistory(10)
                    .Build();

                for (int i = 0; i < 5; i++)
                {
                    int captured = i;
                    invoker.Execute(new Command<CommandUnit>(
                        execute: _ => { }, name: $"Old#{captured}"), CommandUnit.Default);
                }
                // index 0~4

                invoker.JumpTo(1); // currentIndex=1

                await invoker.ExecuteAsync(new Command<CommandUnit>(
                    execute: _ => { }, name: "New"), CommandUnit.Default);

                bool ok = invoker.HistoryCount == 3
                          && invoker.CurrentIndex == 2
                          && invoker.GetName(0) == "Old#0"
                          && invoker.GetName(1) == "Old#1"
                          && invoker.GetName(2) == "New";

                if (ok) Pass("JumpTo 후 Execute: 미래 3개 폐기, New가 index 2");
                else    Fail("JumpTo 후 Execute 분기",
                             $"count={invoker.HistoryCount} idx={invoker.CurrentIndex} " +
                             $"names=[{invoker.GetName(0)},{invoker.GetName(1)},{(invoker.HistoryCount > 2 ? invoker.GetName(2) : "?")}]");

                invoker.Dispose();
            }

            // ── 테스트 6: MaxSize 초과 — 오래된 항목 자동 제거 ──
            // maxSize=3으로 10개 실행. 마지막 3개만 남아야 함
            Debug.Log("[Stage05] ▶ 테스트 6: MaxSize 초과 — 오래된 항목 자동 제거");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithHistory(maxSize: 3)
                    .Build();

                for (int i = 0; i < 10; i++)
                {
                    int captured = i;
                    invoker.Execute(new Command<CommandUnit>(
                        execute: _ => { }, name: $"Cmd#{captured}"), CommandUnit.Default);
                }

                bool ok = invoker.HistoryCount == 3
                          && invoker.CurrentIndex == 2
                          && invoker.GetName(0) == "Cmd#7"
                          && invoker.GetName(1) == "Cmd#8"
                          && invoker.GetName(2) == "Cmd#9";

                if (ok) Pass("MaxSize=3, 10개 실행 → 마지막 3개(Cmd#7~9) 유지");
                else    Fail("MaxSize 초과",
                             $"count={invoker.HistoryCount} idx={invoker.CurrentIndex} " +
                             $"names=[{invoker.GetName(0)},{invoker.GetName(1)},{invoker.GetName(2)}]");

                invoker.Dispose();
            }

            // ── 테스트 7: MaxSize 초과 + Undo → 포인터 보정 ──
            // maxSize=3으로 5개 실행 후 Undo 2회 → CurrentIndex가 음수가 되지 않아야 함
            Debug.Log("[Stage05] ▶ 테스트 7: MaxSize 초과 후 Undo — 포인터 보정");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithHistory(maxSize: 3)
                    .Build();

                int val = 0;
                for (int i = 1; i <= 5; i++)
                {
                    int captured = i;
                    invoker.Execute(new Command<CommandUnit>(
                        execute: _ => val = captured,
                        undo: _ => val = captured - 1,
                        name: $"Set#{captured}"), CommandUnit.Default);
                }
                // 남은 히스토리: Set#3(idx0), Set#4(idx1), Set#5(idx2), currentIndex=2, val=5

                await invoker.UndoAsync(); // Set#5 undo → val=4, currentIndex=1
                await invoker.UndoAsync(); // Set#4 undo → val=3, currentIndex=0

                bool ok = invoker.CurrentIndex == 0
                          && invoker.HistoryCount == 3
                          && val == 3;

                if (ok) Pass("MaxSize 초과 후 Undo 2회: idx=0, val=3");
                else    Fail("MaxSize 후 Undo", $"idx={invoker.CurrentIndex} count={invoker.HistoryCount} val={val}");

                // idx=0에서 한 번 더 Undo → 유효 (idx→-1), 그 다음 Undo → InvalidOperationException
                await invoker.UndoAsync(); // idx=0 → idx=-1 (valid)

                bool gotException = false;
                try { await invoker.UndoAsync(); } // idx=-1 → 예외
                catch (InvalidOperationException) { gotException = true; }

                if (gotException) Pass("MaxSize 후 최소 인덱스에서 Undo → InvalidOperationException");
                else              Fail("MaxSize 최소 Undo 예외", "InvalidOperationException 미발생");

                invoker.Dispose();
            }

            // ── 테스트 8: Undo/Redo 연타 — Switch로 마지막만 완료 ──
            // 3개 Execute 후 UndoAsync 3회 연타 → 마지막 Undo만 완료(앞 2개 Cancelled)
            Debug.Log("[Stage05] ▶ 테스트 8: Undo 연타 Switch — 마지막만 완료");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Switch)
                    .WithHistory(10)
                    .Build();

                int val = 0;
                for (int i = 1; i <= 3; i++)
                {
                    int captured = i;
                    await invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                        execute: async (_, ct) => { await UniTask.Delay(10, cancellationToken: ct); val = captured; },
                        undo: async (_, ct) => { await UniTask.Delay(200, cancellationToken: ct); val = captured - 1; },
                        name: $"Cmd#{captured}"), CommandUnit.Default);
                }
                // val=3, currentIndex=2

                // Undo 2회 연타 (각 200ms짜리) — Switch로 첫 번째 취소, 두 번째만 완료
                // idx=2→1: u1 시작, idx=1→0: u1 취소 후 u2 시작
                var u1 = invoker.UndoAsync(); // currentIndex=2→1, Undo Cmd#3 (200ms) 시작
                var u2 = invoker.UndoAsync(); // currentIndex=1→0, Cmd#3 Undo 취소, Undo Cmd#2 (200ms) 시작

                var r1 = await u1;
                var r2 = await u2;

                await UniTask.Delay(250); // Undo Cmd#2 완료 대기

                bool ok = r1 == ExecutionResult.Cancelled
                          && r2 == ExecutionResult.Completed
                          && invoker.CurrentIndex == 0
                          && val == 1; // Cmd#2 undo: val = 2-1 = 1

                if (ok) Pass("Undo 연타: u1=Cancelled, u2=Completed, idx=0, val=1");
                else    Fail("Undo 연타 Switch", $"r1={r1} r2={r2} idx={invoker.CurrentIndex} val={val}");

                invoker.Dispose();
            }

            // ── 테스트 9: Parallel + History — 모두 동시 실행, 모두 히스토리 기록 ──
            // 5개 동시 실행 → 모두 히스토리에 기록되지만 완료 순서는 무관
            Debug.Log("[Stage05] ▶ 테스트 9: Parallel + History — 동시 실행, 전부 기록");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Parallel)
                    .WithHistory(10)
                    .Build();

                var tasks = new List<UniTask<ExecutionResult>>();
                for (int i = 0; i < 5; i++)
                {
                    int captured = i;
                    tasks.Add(invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                        execute: async (_, ct) => await UniTask.Delay(50, cancellationToken: ct),
                        name: $"Par#{captured}"), CommandUnit.Default));
                }

                await UniTask.WhenAll(tasks);

                // 5개 모두 OnBeforeExecute 통과 → 히스토리에 기록
                bool ok = invoker.HistoryCount == 5 && invoker.CurrentIndex == 4;

                // 이름이 모두 있는지 확인 (순서는 실행 시작 순서 = 호출 순서)
                var names = new HashSet<string>();
                for (int i = 0; i < invoker.HistoryCount; i++)
                    names.Add(invoker.GetName(i));

                for (int i = 0; i < 5; i++)
                    ok = ok && names.Contains($"Par#{i}");

                if (ok) Pass("Parallel 5개: HistoryCount=5, 모든 이름 기록됨");
                else    Fail("Parallel 히스토리", $"count={invoker.HistoryCount}");

                invoker.Dispose();
            }

            // ── 테스트 10: 동기 + 비동기 혼합, Undo/Redo 교차 ──
            // sync A → async B(200ms) → sync C → Undo B → Redo B → 최종 상태 검증
            Debug.Log("[Stage05] ▶ 테스트 10: 동기·비동기 혼합 + Undo/Redo 교차");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Switch)
                    .WithHistory(10)
                    .Build();

                var log = new List<string>();

                // A (sync)
                await invoker.ExecuteAsync(new Command<CommandUnit>(
                    execute: (_, phase) => log.Add($"A-exec({phase})"),
                    undo: _ => log.Add("A-undo"),
                    name: "A"), CommandUnit.Default);

                // B (async 200ms)
                await invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                    execute: async (_, ct) =>
                    {
                        await UniTask.Delay(100, cancellationToken: ct);
                        log.Add("B-exec");
                    },
                    undo: async (_, ct) =>
                    {
                        await UniTask.Delay(50, cancellationToken: ct);
                        log.Add("B-undo");
                    },
                    name: "B"), CommandUnit.Default);

                // C (sync)
                await invoker.ExecuteAsync(new Command<CommandUnit>(
                    execute: _ => log.Add("C-exec"),
                    undo: _ => log.Add("C-undo"),
                    name: "C"), CommandUnit.Default);

                // Undo C
                await invoker.UndoAsync();
                // Undo B
                await invoker.UndoAsync();
                // Redo B
                await invoker.RedoAsync();

                // 기대 log: A-exec(Execute), B-exec, C-exec, C-undo, B-undo, B-exec
                // (Redo B는 ExecutionPhase.Redo이지만 람다는 동일)
                bool ok = invoker.CurrentIndex == 1
                          && invoker.HistoryCount == 3
                          && log.Count == 6
                          && log[0] == "A-exec(Execute)"
                          && log[1] == "B-exec"
                          && log[2] == "C-exec"
                          && log[3] == "C-undo"
                          && log[4] == "B-undo"
                          && log[5] == "B-exec";

                if (ok) Pass("동기·비동기 혼합 Undo/Redo: 로그 순서 일치");
                else    Fail("혼합 Undo/Redo 로그",
                             $"log=[{string.Join(",", log)}] idx={invoker.CurrentIndex}");

                invoker.Dispose();
            }

            // ── 테스트 11: OnHistoryChanged 이벤트 완전 시퀀스 추적 ──
            // Execute×3 → Undo×2 → Redo×1 → JumpTo(0) → Pop → Clear
            // 각 동작마다 이벤트 타입과 인덱스를 기록하여 명세와 대조
            Debug.Log("[Stage05] ▶ 테스트 11: OnHistoryChanged 전체 시퀀스 추적");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Switch)
                    .WithHistory(10)
                    .Build();

                var events = new List<(HistoryActionType action, int index)>();
                invoker.OnHistoryChanged += (action, idx, _) => events.Add((action, idx));

                // Execute×3
                for (int i = 0; i < 3; i++)
                    invoker.Execute(new Command<CommandUnit>(execute: _ => { }, name: $"C{i}"), CommandUnit.Default);

                await invoker.UndoAsync(); // idx: 2→1
                await invoker.UndoAsync(); // idx: 1→0
                await invoker.RedoAsync(); // idx: 0→1
                invoker.JumpTo(0);         // Jump → idx: 0 (단일 이벤트)
                invoker.Pop();             // 최신 아님(idx=0) → 이벤트만, idx=0
                invoker.Clear();           // idx=-1

                var expected = new (HistoryActionType, int)[]
                {
                    (HistoryActionType.Execute, 0),
                    (HistoryActionType.Execute, 1),
                    (HistoryActionType.Execute, 2),
                    (HistoryActionType.Undo,    1),
                    (HistoryActionType.Undo,    0),
                    (HistoryActionType.Redo,    1),
                    (HistoryActionType.Jump,    0),
                    (HistoryActionType.Pop,     0),
                    (HistoryActionType.Clear,  -1),
                };

                bool ok = events.Count == expected.Length;
                for (int i = 0; i < expected.Length && ok; i++)
                    ok = events[i] == expected[i];

                if (ok) Pass("OnHistoryChanged 시퀀스 9개 전부 일치");
                else
                {
                    var actual = string.Join(", ", events.ConvertAll(e => $"({e.action},{e.index})"));
                    var exp    = string.Join(", ", Array.ConvertAll(expected, e => $"({e.Item1},{e.Item2})"));
                    Fail("OnHistoryChanged 시퀀스", $"\n  expected: {exp}\n  actual:   {actual}");
                }

                invoker.Dispose();
            }

            // ── 테스트 12: Dispose 진행 중 — 실행 중 + 큐 대기 모두 정리 ──
            // Sequential: A 실행 중(1초), B·C 큐 대기 → Dispose → 모두 Cancelled + ObjectDisposedException
            Debug.Log("[Stage05] ▶ 테스트 12: Dispose — 실행 중 + 큐 대기 전부 정리");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Sequential)
                    .WithHistory(10)
                    .Build();

                var taskA = invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                    execute: async (_, ct) => await UniTask.Delay(1000, cancellationToken: ct),
                    name: "A"), CommandUnit.Default);
                var taskB = invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                    execute: async (_, ct) => await UniTask.Delay(50, cancellationToken: ct),
                    name: "B"), CommandUnit.Default);
                var taskC = invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                    execute: async (_, ct) => await UniTask.Delay(50, cancellationToken: ct),
                    name: "C"), CommandUnit.Default);

                await UniTask.Delay(50); // A 실행 시작 대기
                invoker.Dispose();

                var results = await UniTask.WhenAll(taskA, taskB, taskC);

                bool allCancelled = results.Item1 == ExecutionResult.Cancelled
                                    && results.Item2 == ExecutionResult.Cancelled
                                    && results.Item3 == ExecutionResult.Cancelled;

                // Dispose 후 접근 시 ObjectDisposedException
                bool gotDisposed = false;
                try { invoker.Execute(new Command<CommandUnit>(execute: _ => { }), CommandUnit.Default); }
                catch (ObjectDisposedException) { gotDisposed = true; }

                if (allCancelled && gotDisposed)
                    Pass("Dispose: 실행 중·큐 대기 모두 Cancelled, 이후 ObjectDisposedException");
                else
                    Fail("Dispose 정리", $"results=({results.Item1},{results.Item2},{results.Item3}) disposed={gotDisposed}");
            }

            // ── 최종 집계 ───────────────────────────────────────────────────────
            string summary = _failed == 0
                ? $"[Stage05] ✅ 완료 — {_passed}개 통과 / 0개 실패"
                : $"[Stage05] ❌ 완료 — {_passed}개 통과 / {_failed}개 실패";
            Debug.Log(summary);
        }
    }
}
