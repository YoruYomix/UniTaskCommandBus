using System.Collections.Generic;
using UnityEngine;
using Cysharp.Threading.Tasks;
using UniTaskCommandBus;
using Debug = UnityEngine.Debug;

namespace UniTaskCommandBus.Samples
{
    public class Stage06_SuperStressSample : MonoBehaviour
    {
        private int _passed;
        private int _failed;

        private void Pass(string label)
        {
            _passed++;
            Debug.Log($"[Stage06]   ✅ {label} 통과");
        }

        private void Fail(string label, string detail)
        {
            _failed++;
            Debug.Log($"[Stage06]   ❌ [실패] {label}: {detail}");
        }

        private void Start() => RunAsync().Forget();

        private async UniTaskVoid RunAsync()
        {
            // ── 테스트 1: Switch Execute 진행 중 → UndoAsync → Execute lambda 취소 ──
            // Switch 정책에서 진행 중인 Execute가 UndoAsync의 ReplaceCts()로 취소된다.
            // Execute C는 히스토리에는 남지만 lambda는 중단됨.
            Debug.Log("[Stage06] ▶ 테스트 1: Switch Execute 진행 중 → UndoAsync → Execute lambda 취소");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Switch)
                    .WithHistory(10)
                    .Build();

                int val = 0;
                await invoker.ExecuteAsync(new Command<CommandUnit>(execute: _ => val = 1, undo: _ => val = 0, name: "A"), CommandUnit.Default);
                await invoker.ExecuteAsync(new Command<CommandUnit>(execute: _ => val = 2, undo: _ => val = 1, name: "B"), CommandUnit.Default);
                // history=[A,B], idx=1, val=2

                bool cLambdaStarted = false;
                var cTask = invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                    execute: async (_, ct) =>
                    {
                        cLambdaStarted = true;
                        val = 99;
                        await UniTask.Delay(500, cancellationToken: ct);
                        val = 3; // 취소로 인해 도달하지 못해야 함
                    },
                    name: "C"), CommandUnit.Default);
                // Switch → C 즉시 기록됨. history=[A,B,C], idx=2

                await UniTask.Delay(50); // C lambda 시작 후 (val=99)

                // UndoAsync: ReplaceCts → C lambda 취소, idx=2→1, B's undo(동기): val=1
                var undoTask = invoker.UndoAsync();

                var cResult = await cTask;
                var undoResult = await undoTask;

                // UndoAsync는 idx=2의 C를 Undo. C의 undo=null(no-op)이므로 val 변화 없음.
                bool ok = cResult == ExecutionResult.Cancelled
                          && undoResult == ExecutionResult.Completed
                          && cLambdaStarted
                          && invoker.CurrentIndex == 1
                          && invoker.HistoryCount == 3; // C는 lambda 취소됐어도 히스토리 유지

                if (ok) Pass("Switch Execute 취소 + Undo 완료 (C 히스토리 유지, idx=1, count=3)");
                else    Fail("Switch Execute→Undo", $"cResult={cResult} undoResult={undoResult} idx={invoker.CurrentIndex} count={invoker.HistoryCount} cStarted={cLambdaStarted}");

                invoker.Dispose();
            }

            // ── 테스트 2: UndoAsync 진행 중 → Switch Execute → Undo lambda 취소 + 분기 삭제 ──
            // Execute(Switch)의 ReplaceCts()가 진행 중인 Undo lambda를 취소한다.
            // 분기 규칙: idx=0에서 Execute C → B 제거 → history=[A,C].
            Debug.Log("[Stage06] ▶ 테스트 2: UndoAsync 진행 중 → Switch Execute → Undo 취소 + 분기 삭제");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Switch)
                    .WithHistory(10)
                    .Build();

                int val = 0;
                await invoker.ExecuteAsync(new Command<CommandUnit>(execute: _ => val = 1, undo: _ => val = 0, name: "A"), CommandUnit.Default);
                await invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                    execute: async (_, ct) => { await UniTask.Delay(10, cancellationToken: ct); val = 2; },
                    undo: async (_, ct) => { await UniTask.Delay(300, cancellationToken: ct); val = 1; },
                    name: "B"), CommandUnit.Default);
                // history=[A,B], idx=1, val=2

                var undoTask = invoker.UndoAsync(); // idx=1→0, B undo 시작 (300ms)
                await UniTask.Delay(50);            // B undo lambda 진행 중

                // Execute C(Switch): ReplaceCts → B undo 취소
                // 분기: idx=0 < count-1(1) → B 제거 → [A], 이후 C 추가 → [A,C], idx=1
                var cTask = invoker.ExecuteAsync(new Command<CommandUnit>(execute: _ => val = 3, name: "C"), CommandUnit.Default);

                var undoResult = await undoTask;
                await cTask;

                bool ok = undoResult == ExecutionResult.Cancelled
                          && invoker.CurrentIndex == 1
                          && invoker.HistoryCount == 2
                          && invoker.GetName(0) == "A"
                          && invoker.GetName(1) == "C"
                          && val == 3;

                if (ok) Pass("Undo 취소 + Execute 분기 (B 제거, C 추가, val=3)");
                else    Fail("Undo→Execute 분기", $"undoResult={undoResult} idx={invoker.CurrentIndex} count={invoker.HistoryCount} names=[{(invoker.HistoryCount > 0 ? invoker.GetName(0) : "?")} {(invoker.HistoryCount > 1 ? invoker.GetName(1) : "?")}] val={val}");

                invoker.Dispose();
            }

            // ── 테스트 3: Sequential 재진입 체인 (A람다→B Execute, B람다→C Execute) ──
            // Sequential 정책에서 execute 람다 내부에서 Execute를 호출하면 큐에 추가됨.
            // A 완료 후 B, B 완료 후 C가 차례로 실행되고 히스토리 순서도 A→B→C.
            Debug.Log("[Stage06] ▶ 테스트 3: Sequential 재진입 체인 — A 람다→B Execute, B 람다→C Execute");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Sequential)
                    .WithHistory(10)
                    .Build();

                var execOrder = new List<string>();

                var cmdC = new Command<CommandUnit>(execute: _ => execOrder.Add("C"), name: "C");
                var cmdB = new Command<CommandUnit>(execute: _ =>
                {
                    execOrder.Add("B");
                    invoker.Execute(cmdC, CommandUnit.Default); // C를 큐에 추가
                }, name: "B");
                var cmdA = new Command<CommandUnit>(execute: _ =>
                {
                    execOrder.Add("A");
                    invoker.Execute(cmdB, CommandUnit.Default); // B를 큐에 추가
                }, name: "A");

                await invoker.ExecuteAsync(cmdA, CommandUnit.Default);
                await UniTask.Delay(50); // B, C 실행 완료 대기

                bool ok = invoker.HistoryCount == 3
                          && invoker.GetName(0) == "A"
                          && invoker.GetName(1) == "B"
                          && invoker.GetName(2) == "C"
                          && execOrder.Count == 3
                          && execOrder[0] == "A"
                          && execOrder[1] == "B"
                          && execOrder[2] == "C";

                if (ok) Pass("Sequential 재진입: A→B→C 실행 순서 및 히스토리 순서 일치");
                else    Fail("Sequential 재진입", $"count={invoker.HistoryCount} order=[{string.Join(",", execOrder)}] names=[{(invoker.HistoryCount > 0 ? invoker.GetName(0) : "?")},{(invoker.HistoryCount > 1 ? invoker.GetName(1) : "?")},{(invoker.HistoryCount > 2 ? invoker.GetName(2) : "?")}]");

                invoker.Dispose();
            }

            // ── 테스트 4: maxSize=1 극단 케이스 ──
            // 크기 1 히스토리에서 Execute마다 overflow가 발생한다.
            // Undo/Redo 후 재Execute도 정상 동작해야 한다.
            Debug.Log("[Stage06] ▶ 테스트 4: maxSize=1 극단 케이스");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithHistory(1)
                    .Build();

                int val = 0;

                invoker.Execute(new Command<CommandUnit>(execute: _ => val = 1, undo: _ => val = 0, name: "A"), CommandUnit.Default);
                bool c1 = invoker.HistoryCount == 1 && invoker.CurrentIndex == 0 && invoker.GetName(0) == "A" && val == 1;

                // B Execute: overflow → A 제거 → [B]
                invoker.Execute(new Command<CommandUnit>(execute: _ => val = 2, undo: _ => val = 1, name: "B"), CommandUnit.Default);
                bool c2 = invoker.HistoryCount == 1 && invoker.CurrentIndex == 0 && invoker.GetName(0) == "B" && val == 2;

                await invoker.UndoAsync(); // B undo: val=1, idx=-1
                bool c3 = invoker.CurrentIndex == -1 && val == 1;

                await invoker.RedoAsync(); // B redo: val=2, idx=0
                bool c4 = invoker.CurrentIndex == 0 && invoker.GetName(0) == "B" && val == 2;

                // C Execute: overflow → B 제거 → [C]
                invoker.Execute(new Command<CommandUnit>(execute: _ => val = 3, undo: _ => val = 2, name: "C"), CommandUnit.Default);
                bool c5 = invoker.HistoryCount == 1 && invoker.GetName(0) == "C" && val == 3;

                await invoker.UndoAsync(); // C undo: val=2, idx=-1
                bool c6 = invoker.CurrentIndex == -1 && val == 2;

                if (c1 && c2 && c3 && c4 && c5 && c6)
                    Pass("maxSize=1: A→B(overflow)→Undo→Redo→C(overflow)→Undo 전 단계 일치");
                else
                    Fail("maxSize=1 극단", $"c1={c1} c2={c2} c3={c3} c4={c4} c5={c5} c6={c6}");

                invoker.Dispose();
            }

            // ── 테스트 5: ThrottleLast 폭격 20개 → First+Last만 히스토리 ──
            // 진행 중인 First 커맨드 이후 20개 연속 → #1~#19 Dropped, #20만 슬롯에 잔류.
            // 완료 후 HistoryCount=2, names=[First, Rapid#20].
            Debug.Log("[Stage06] ▶ 테스트 5: ThrottleLast 폭격 20개 → First+Rapid#20만 히스토리");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.ThrottleLast)
                    .WithHistory(30)
                    .Build();

                int val = 0;
                var firstTask = invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                    execute: async (_, ct) => { await UniTask.Delay(400, cancellationToken: ct); val = 0; },
                    name: "First"), CommandUnit.Default);

                UniTask<ExecutionResult> lastTask = default;
                for (int i = 1; i <= 20; i++)
                {
                    int cap = i;
                    lastTask = invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                        execute: async (_, ct) => { await UniTask.Delay(10, cancellationToken: ct); val = cap; },
                        name: $"Rapid#{cap}"), CommandUnit.Default);
                }
                // Rapid#1~#19 → Dropped, Rapid#20 → 슬롯 대기

                await firstTask;
                await lastTask; // Rapid#20 완료

                bool ok = invoker.HistoryCount == 2
                          && invoker.GetName(0) == "First"
                          && invoker.GetName(1) == "Rapid#20"
                          && val == 20;

                if (ok) Pass("ThrottleLast 폭격: First+Rapid#20 히스토리, Rapid#1~#19 Dropped");
                else    Fail("ThrottleLast 폭격", $"count={invoker.HistoryCount} names=[{(invoker.HistoryCount > 0 ? invoker.GetName(0) : "?")},{(invoker.HistoryCount > 1 ? invoker.GetName(1) : "?")}] val={val}");

                invoker.Dispose();
            }

            // ── 테스트 6: 30개 히스토리 JumpTo 3회 → Jump 이벤트 정확히 3회 ──
            // JumpTo 내부의 Undo/Redo 중간 스텝은 이벤트를 억제하고,
            // 최종 도달 시 Jump 액션으로 단 1회만 발행한다.
            Debug.Log("[Stage06] ▶ 테스트 6: 30개 히스토리 JumpTo 3회 → Jump 이벤트 정확히 3회");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithHistory(30)
                    .Build();

                for (int i = 0; i < 30; i++)
                {
                    int cap = i;
                    invoker.Execute(new Command<CommandUnit>(execute: _ => { }, name: $"E{cap}"), CommandUnit.Default);
                }
                // history=[E0..E29], idx=29

                int jumpCount = 0, totalCount = 0;
                invoker.OnHistoryChanged += (action, _, __) =>
                {
                    totalCount++;
                    if (action == HistoryActionType.Jump) jumpCount++;
                };

                invoker.JumpTo(0);  // +1 Jump
                invoker.JumpTo(29); // +1 Jump
                invoker.JumpTo(14); // +1 Jump

                bool ok = jumpCount == 3 && totalCount == 3;
                if (ok) Pass("JumpTo 3회 → Jump 이벤트 정확히 3회 (중간 Undo/Redo 이벤트 억제)");
                else    Fail("JumpTo 이벤트 카운트", $"jumpCount={jumpCount} totalCount={totalCount}");

                invoker.Dispose();
            }

            // ── 테스트 7: Parallel 10개 동시 → CancelAll → 모두 Cancelled, HistoryCount=10 ──
            // Parallel 정책은 OnBeforeExecute가 즉시 실행되므로 모두 히스토리에 기록된 후 취소된다.
            Debug.Log("[Stage06] ▶ 테스트 7: Parallel 10개 동시 → CancelAll → 모두 Cancelled, HistoryCount=10");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Parallel)
                    .WithHistory(20)
                    .Build();

                var tasks = new List<UniTask<ExecutionResult>>();
                for (int i = 0; i < 10; i++)
                {
                    int cap = i;
                    tasks.Add(invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                        execute: async (_, ct) => { await UniTask.Delay(500, cancellationToken: ct); },
                        name: $"P{cap}"), CommandUnit.Default));
                }

                await UniTask.Delay(50);
                invoker.CancelAll();
                await UniTask.Delay(100); // 취소 전파 대기

                int cancelledCount = 0;
                for (int i = 0; i < tasks.Count; i++)
                {
                    var r = await tasks[i];
                    if (r == ExecutionResult.Cancelled) cancelledCount++;
                }

                bool ok = cancelledCount == 10 && invoker.HistoryCount == 10;
                if (ok) Pass("Parallel 10개 CancelAll → 전부 Cancelled, HistoryCount=10");
                else    Fail("Parallel CancelAll", $"cancelledCount={cancelledCount} historyCount={invoker.HistoryCount}");

                invoker.Dispose();
            }

            // ── 테스트 8: Sequential 큐 5개 → 2번째 실행 중 CancelAll → 1+2만 기록 ──
            // cmd1 완료(~200ms) → cmd2 기록+시작(~200ms) → 250ms에 CancelAll
            // cmd2 lambda 취소, cmd3~5는 큐에서 제거(미기록).
            Debug.Log("[Stage06] ▶ 테스트 8: Sequential 큐 5개 → 2번째 실행 중 CancelAll → count=2");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Sequential)
                    .WithHistory(10)
                    .Build();

                var tasks = new List<UniTask<ExecutionResult>>();
                for (int i = 1; i <= 5; i++)
                {
                    int cap = i;
                    tasks.Add(invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                        execute: async (_, ct) => { await UniTask.Delay(200, cancellationToken: ct); },
                        name: $"Cmd#{cap}"), CommandUnit.Default));
                }
                // Cmd#1 실행 중, Cmd#2~5 큐 대기

                await UniTask.Delay(250); // Cmd#1 완료(200ms) → Cmd#2 기록+시작
                invoker.CancelAll();      // Cmd#2 lambda 취소 + Cmd#3~5 큐 drain

                await UniTask.Delay(50);  // 취소 전파 대기

                var r1 = await tasks[0];
                var r2 = await tasks[1];
                var r3 = await tasks[2];
                var r4 = await tasks[3];
                var r5 = await tasks[4];

                bool ok = r1 == ExecutionResult.Completed
                          && r2 == ExecutionResult.Cancelled
                          && r3 == ExecutionResult.Cancelled
                          && r4 == ExecutionResult.Cancelled
                          && r5 == ExecutionResult.Cancelled
                          && invoker.HistoryCount == 2; // Cmd#1(기록) + Cmd#2(기록 후 취소)

                if (ok) Pass("Sequential CancelAll: Cmd#1=Completed, #2=Cancelled, #3~5=Cancelled, count=2");
                else    Fail("Sequential CancelAll", $"r1={r1} r2={r2} r3={r3} r4={r4} r5={r5} count={invoker.HistoryCount}");

                invoker.Dispose();
            }

            // ── 테스트 9: Undo/Redo 교차 연타 — 마지막 Redo만 완료 ──
            // u1→u2→r1→r2 연속 호출. 각 호출이 이전 TCS를 즉시 Cancelled로 확정하고
            // 마지막 r2의 lambda만 실제로 실행된다.
            Debug.Log("[Stage06] ▶ 테스트 9: Undo/Redo 교차 연타 — u1/u2/r1=Cancelled, r2=Completed");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Switch)
                    .WithHistory(10)
                    .Build();

                int val = 0;
                for (int i = 1; i <= 5; i++)
                {
                    int cap = i;
                    await invoker.ExecuteAsync(new AsyncCommand<CommandUnit>(
                        execute: async (_, ct) => { await UniTask.Delay(10, cancellationToken: ct); val = cap; },
                        undo: async (_, ct) => { await UniTask.Delay(200, cancellationToken: ct); val = cap - 1; },
                        name: $"Cmd#{cap}"), CommandUnit.Default);
                }
                // val=5, idx=4

                // 동기 연속 호출: 각 호출이 이전 undoRedoTcs를 즉시 Cancelled 확정
                var u1 = invoker.UndoAsync(); // idx=4→3, Undo Cmd#5 시작 (200ms)
                var u2 = invoker.UndoAsync(); // idx=3→2, u1 즉시 Cancelled, Undo Cmd#4 시작
                var r1 = invoker.RedoAsync(); // idx=2→3, u2 즉시 Cancelled, Redo Cmd#4 시작
                var r2 = invoker.RedoAsync(); // idx=3→4, r1 즉시 Cancelled, Redo Cmd#5 시작 (200ms)

                var ru1 = await u1;
                var ru2 = await u2;
                var rr1 = await r1;
                var rr2 = await r2; // ~200ms 대기

                await UniTask.Delay(50); // 안전 마진

                bool ok = ru1 == ExecutionResult.Cancelled
                          && ru2 == ExecutionResult.Cancelled
                          && rr1 == ExecutionResult.Cancelled
                          && rr2 == ExecutionResult.Completed
                          && invoker.CurrentIndex == 4
                          && val == 5;

                if (ok) Pass("Undo/Redo 교차 연타: u1/u2/r1=Cancelled, r2=Completed, idx=4, val=5");
                else    Fail("Undo/Redo 교차 연타", $"u1={ru1} u2={ru2} r1={rr1} r2={rr2} idx={invoker.CurrentIndex} val={val}");

                invoker.Dispose();
            }

            // ── 테스트 10: 전체 API 종합 시나리오 ──
            // Switch+History: 10 Execute → JumpTo(3) → 분기 3 Execute → Undo 2 → Redo 1
            // → Pop(탐색 중) → Clear → Execute G0. 이벤트 총 20회 검증.
            Debug.Log("[Stage06] ▶ 테스트 10: 전체 API 종합 시나리오");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Switch)
                    .WithHistory(10)
                    .Build();

                int eventCount = 0;
                invoker.OnHistoryChanged += (_, __, ___) => eventCount++;

                for (int i = 0; i < 10; i++)
                {
                    int cap = i;
                    invoker.Execute(new Command<CommandUnit>(execute: _ => { }, name: $"E{cap}"), CommandUnit.Default);
                }
                // history=[E0..E9], idx=9, eventCount=10
                bool c1 = invoker.HistoryCount == 10 && invoker.CurrentIndex == 9 && eventCount == 10;

                invoker.JumpTo(3);
                // eventCount=11 (Jump 1회)
                bool c2 = invoker.CurrentIndex == 3 && eventCount == 11;

                // 분기: E4~E9 제거 → F0,F1,F2 추가 → [E0,E1,E2,E3,F0,F1,F2], count=7, idx=6
                invoker.Execute(new Command<CommandUnit>(execute: _ => { }, name: "F0"), CommandUnit.Default);
                invoker.Execute(new Command<CommandUnit>(execute: _ => { }, name: "F1"), CommandUnit.Default);
                invoker.Execute(new Command<CommandUnit>(execute: _ => { }, name: "F2"), CommandUnit.Default);
                bool c3 = invoker.HistoryCount == 7 && invoker.CurrentIndex == 6 && eventCount == 14;

                await invoker.UndoAsync(); // idx=5
                await invoker.UndoAsync(); // idx=4
                bool c4 = invoker.CurrentIndex == 4 && eventCount == 16;

                await invoker.RedoAsync(); // idx=5
                bool c5 = invoker.CurrentIndex == 5 && eventCount == 17;

                // Pop: idx=5, count-1=6 → 탐색 중 → 이벤트만 발행, 리스트 변화 없음
                invoker.Pop();
                bool c6 = invoker.HistoryCount == 7 && invoker.CurrentIndex == 5 && eventCount == 18;

                invoker.Clear();
                bool c7 = invoker.HistoryCount == 0 && invoker.CurrentIndex == -1 && eventCount == 19;

                invoker.Execute(new Command<CommandUnit>(execute: _ => { }, name: "G0"), CommandUnit.Default);
                bool c8 = invoker.HistoryCount == 1 && invoker.CurrentIndex == 0
                          && invoker.GetName(0) == "G0" && eventCount == 20;

                if (c1 && c2 && c3 && c4 && c5 && c6 && c7 && c8)
                    Pass("전체 API 종합: 10 Execute→JumpTo→분기 3→Undo 2→Redo 1→Pop→Clear→Execute, 이벤트 20회");
                else
                    Fail("전체 API 종합", $"c1={c1} c2={c2} c3={c3} c4={c4} c5={c5} c6={c6} c7={c7} c8={c8} events={eventCount}");

                invoker.Dispose();
            }

            // ── 최종 집계 ─────────────────────────────────────────────────────
            string summary = _failed == 0
                ? $"[Stage06] ✅ 완료 — {_passed}개 통과 / 0개 실패"
                : $"[Stage06] ❌ 완료 — {_passed}개 통과 / {_failed}개 실패";
            Debug.Log(summary);
        }
    }
}
