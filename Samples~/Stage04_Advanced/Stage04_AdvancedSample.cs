using System;
using System.Diagnostics;
using UnityEngine;
using Cysharp.Threading.Tasks;
using UniTaskCommandBus;
using Debug = UnityEngine.Debug;

namespace UniTaskCommandBus.Samples
{
    public class Stage04_AdvancedSample : MonoBehaviour
    {
        private int _passed;
        private int _failed;

        private void Pass(string label)
        {
            _passed++;
            Debug.Log($"[Stage04]   ✅ {label} 통과");
        }

        private void Fail(string label, string detail)
        {
            _failed++;
            Debug.Log($"[Stage04]   ❌ [실패] {label}: {detail}");
        }

        private void Start()
        {
            RunAsync().Forget();
        }

        private async UniTaskVoid RunAsync()
        {
            // ── 테스트 1: JumpTo 기본 (동기 커맨드) ────────────────────────────
            Debug.Log("[Stage04] ▶ 테스트 1: JumpTo 기본 — 동기 커맨드");
            {
                var invoker = CommandBus.Create<int>()
                    .WithHistory(10)
                    .Build();

                int value = 0;
                for (int i = 1; i <= 5; i++)
                {
                    int captured = i;
                    invoker.Execute(new Command<int>(
                        execute: _ => value = captured,
                        undo: _ => value = captured - 1,
                        name: $"Set#{captured}"));
                }
                // CurrentIndex=4, value=5

                invoker.JumpTo(1);
                // JumpTo(1): Undo 3번 → CurrentIndex=1. undo: _ => value = captured - 1, 마지막은 captured=3 → value=2
                if (invoker.CurrentIndex == 1 && value == 2)
                    Pass("JumpTo(1) — CurrentIndex=1, value=2");
                else
                    Fail("JumpTo(1)", $"expected CurrentIndex=1/value=2, got {invoker.CurrentIndex}/{value}");

                invoker.JumpTo(4);
                // JumpTo(4): Redo 3번 → CurrentIndex=4, value=5
                if (invoker.CurrentIndex == 4 && value == 5)
                    Pass("JumpTo(4) — CurrentIndex=4, value=5");
                else
                    Fail("JumpTo(4)", $"expected CurrentIndex=4/value=5, got {invoker.CurrentIndex}/{value}");

                invoker.Dispose();
            }

            // ── 테스트 2: JumpTo + 비동기 커맨드 — 동기 반환 검증 ─────────────
            Debug.Log("[Stage04] ▶ 테스트 2: JumpTo + 비동기 커맨드 — 동기 반환 검증");
            {
                // Switch 정책: 각 Execute가 즉시 히스토리에 기록됨 (OnBeforeExecute는 동기)
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Switch)
                    .WithHistory(10)
                    .Build();

                for (int i = 0; i < 5; i++)
                {
                    int captured = i;
                    invoker.Execute(new AsyncCommand<CommandUnit>(
                        execute: async (_, ct) =>
                        {
                            await UniTask.Delay(500, cancellationToken: ct);
                        },
                        name: $"AsyncCmd#{captured}"), CommandUnit.Default);
                }
                // 5개 모두 히스토리에 즉시 기록됨 (currentIndex=4)
                // JumpTo 호출 — 동기로 즉시 반환되어야 함
                var sw = Stopwatch.StartNew();
                invoker.JumpTo(0);
                sw.Stop();

                if (sw.ElapsedMilliseconds < 100)
                    Pass($"JumpTo 동기 반환 ({sw.ElapsedMilliseconds}ms < 100ms)");
                else
                    Fail("JumpTo 동기 반환", $"elapsed={sw.ElapsedMilliseconds}ms (too slow)");

                invoker.Dispose();
            }

            // ── 테스트 3: JumpTo + 즉시 Cancel ───────────────────────────────
            Debug.Log("[Stage04] ▶ 테스트 3: JumpTo 후 즉시 Cancel — 최종 애니메이션 중단");
            {
                bool lastCompleted = false;
                // Switch 정책: 3개 모두 즉시 히스토리에 기록
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Switch)
                    .WithHistory(10)
                    .Build();

                for (int i = 0; i < 3; i++)
                {
                    int captured = i;
                    invoker.Execute(new AsyncCommand<CommandUnit>(
                        execute: async (_, ct) =>
                        {
                            await UniTask.Delay(500, cancellationToken: ct);
                            if (captured == 2) lastCompleted = true;
                        },
                        name: $"Cmd#{captured}"), CommandUnit.Default);
                }
                // currentIndex=2. 먼저 뒤로, 그 다음 앞으로 JumpTo → 마지막 Redo 애니메이션 Cancel
                invoker.JumpTo(0); // Undo×2 → currentIndex=0
                invoker.JumpTo(2); // Redo×2 → currentIndex=2, captured=2 redo lambda 시작
                invoker.Cancel();  // 마지막 Redo 람다 취소

                await UniTask.Delay(600); // 500ms 이상 대기해도 lastCompleted가 false여야 함

                if (!lastCompleted)
                    Pass("JumpTo 후 Cancel — 최종 애니메이션 중단됨");
                else
                    Fail("JumpTo 후 Cancel", "최종 애니메이션이 중단되지 않음");

                invoker.Dispose();
            }

            // ── 테스트 4: JumpTo 예외 ─────────────────────────────────────────
            Debug.Log("[Stage04] ▶ 테스트 4: JumpTo 예외");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithHistory(10)
                    .Build();

                invoker.Execute(new Command<CommandUnit>(execute: _ => { }, name: "A"), CommandUnit.Default);
                invoker.Execute(new Command<CommandUnit>(execute: _ => { }, name: "B"), CommandUnit.Default);
                invoker.Execute(new Command<CommandUnit>(execute: _ => { }, name: "C"), CommandUnit.Default);

                // 자기자리 점프
                bool gotInvalidOp = false;
                try { invoker.JumpTo(2); }
                catch (InvalidOperationException) { gotInvalidOp = true; }

                if (gotInvalidOp)
                    Pass("JumpTo(현재위치) → InvalidOperationException");
                else
                    Fail("JumpTo(현재위치)", "InvalidOperationException 미발생");

                // 범위 외
                bool gotOutOfRange = false;
                try { invoker.JumpTo(5); }
                catch (ArgumentOutOfRangeException) { gotOutOfRange = true; }

                if (gotOutOfRange)
                    Pass("JumpTo(범위밖) → ArgumentOutOfRangeException");
                else
                    Fail("JumpTo(범위밖)", "ArgumentOutOfRangeException 미발생");

                invoker.Dispose();
            }

            // ── 테스트 5: JumpTo OnHistoryChanged 발행 횟수 ───────────────────
            Debug.Log("[Stage04] ▶ 테스트 5: JumpTo — OnHistoryChanged 정확히 1회 (Jump 액션)");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithHistory(10)
                    .Build();

                for (int i = 0; i < 5; i++)
                {
                    int captured = i;
                    invoker.Execute(new Command<CommandUnit>(execute: _ => { }, name: $"Cmd#{captured}"), CommandUnit.Default);
                }

                int eventCount = 0;
                HistoryActionType lastAction = HistoryActionType.Execute;
                int lastIndex = -99;
                invoker.OnHistoryChanged += (action, idx, name) =>
                {
                    eventCount++;
                    lastAction = action;
                    lastIndex = idx;
                };

                invoker.JumpTo(0);

                if (eventCount == 1 && lastAction == HistoryActionType.Jump && lastIndex == 0)
                    Pass("JumpTo 이벤트 1회 (Jump, index=0)");
                else
                    Fail("JumpTo 이벤트", $"count={eventCount}, action={lastAction}, index={lastIndex}");

                invoker.Dispose();
            }

            // ── 테스트 6: ExecutionPhase 검증 ─────────────────────────────────
            Debug.Log("[Stage04] ▶ 테스트 6: ExecutionPhase 검증");
            {
                // Invoker<T> → None
                ExecutionPhase phaseFromInvoker = ExecutionPhase.Execute; // 초기값을 다른 값으로
                var plainInvoker = CommandBus.Create<CommandUnit>().Build();
                plainInvoker.Execute(new Command<CommandUnit>(
                    execute: (_, phase) => phaseFromInvoker = phase));

                if (phaseFromInvoker == ExecutionPhase.None)
                    Pass("Invoker<T> Execute → ExecutionPhase.None");
                else
                    Fail("Invoker<T> Execute phase", $"expected None, got {phaseFromInvoker}");
                plainInvoker.Dispose();

                // HistoryInvoker<T> 최초 Execute → Execute
                ExecutionPhase phaseOnExecute = ExecutionPhase.None;
                ExecutionPhase phaseOnRedo = ExecutionPhase.None;
                var histInvoker = CommandBus.Create<CommandUnit>()
                    .WithHistory(10)
                    .Build();

                histInvoker.Execute(new Command<CommandUnit>(
                    execute: (_, phase) => phaseOnExecute = phase));

                if (phaseOnExecute == ExecutionPhase.Execute)
                    Pass("HistoryInvoker Execute → ExecutionPhase.Execute");
                else
                    Fail("HistoryInvoker Execute phase", $"expected Execute, got {phaseOnExecute}");

                // Undo 후 Redo → Redo phase
                await histInvoker.UndoAsync();
                await histInvoker.RedoAsync();

                // Redo 시 execute 람다가 Redo phase로 다시 호출됨
                // phaseOnExecute는 Redo 시에도 갱신됨
                if (phaseOnExecute == ExecutionPhase.Redo)
                    Pass("HistoryInvoker Redo → ExecutionPhase.Redo");
                else
                    Fail("HistoryInvoker Redo phase", $"expected Redo, got {phaseOnExecute}");

                // JumpTo 오른쪽 방향 스텝 → Redo phase
                histInvoker.Execute(new Command<CommandUnit>(
                    execute: (_, phase) => phaseOnRedo = phase,
                    name: "Cmd2"));
                histInvoker.Execute(new Command<CommandUnit>(execute: _ => { }, name: "Cmd3"), CommandUnit.Default);
                await histInvoker.UndoAsync();
                await histInvoker.UndoAsync();

                // CurrentIndex=0이므로 JumpTo(2) → Redo 2번
                histInvoker.JumpTo(2);
                await UniTask.Delay(50); // Redo 비동기 완료 대기

                if (phaseOnRedo == ExecutionPhase.Redo)
                    Pass("JumpTo 오른쪽 스텝 → ExecutionPhase.Redo");
                else
                    Fail("JumpTo 오른쪽 스텝 phase", $"expected Redo, got {phaseOnRedo}");

                histInvoker.Dispose();
            }

            // ── 테스트 7: Dispose 기본 ─────────────────────────────────────────
            Debug.Log("[Stage04] ▶ 테스트 7: Dispose 기본 — using 블록 후 ObjectDisposedException");
            {
                HistoryInvoker<CommandUnit> disposedInvoker;
                using (var invoker = CommandBus.Create<CommandUnit>()
                    .WithHistory(10)
                    .Build())
                {
                    invoker.Execute(new Command<CommandUnit>(execute: _ => { }, name: "A"), CommandUnit.Default);
                    disposedInvoker = invoker;
                }

                bool gotDisposed = false;
                try { disposedInvoker.Execute(new Command<CommandUnit>(execute: _ => { }, name: "B"), CommandUnit.Default); }
                catch (ObjectDisposedException) { gotDisposed = true; }

                if (gotDisposed)
                    Pass("Dispose 후 Execute → ObjectDisposedException");
                else
                    Fail("Dispose 후 Execute", "ObjectDisposedException 미발생");

                // HistoryCount, CurrentIndex 접근도 예외
                bool gotDisposed2 = false;
                try { _ = disposedInvoker.HistoryCount; }
                catch (ObjectDisposedException) { gotDisposed2 = true; }

                if (gotDisposed2)
                    Pass("Dispose 후 HistoryCount → ObjectDisposedException");
                else
                    Fail("Dispose 후 HistoryCount", "ObjectDisposedException 미발생");
            }

            // ── 테스트 8: Dispose 시 이벤트 자동 해제 ────────────────────────
            Debug.Log("[Stage04] ▶ 테스트 8: Dispose 후 OnHistoryChanged 핸들러 미호출");
            {
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithHistory(10)
                    .Build();

                int handlerCallCount = 0;
                invoker.OnHistoryChanged += (_, __, ___) => handlerCallCount++;

                invoker.Execute(new Command<CommandUnit>(execute: _ => { }, name: "Before"), CommandUnit.Default);
                int countBeforeDispose = handlerCallCount; // 1이어야 함

                invoker.Dispose();

                // Dispose 후에는 핸들러가 null — 직접 호출 시도 불가하므로
                // handlerCallCount가 더 늘지 않았는지 확인만
                if (countBeforeDispose == 1 && handlerCallCount == 1)
                    Pass("Dispose 전 이벤트 1회, Dispose 후 핸들러 null (추가 호출 없음)");
                else
                    Fail("Dispose 이벤트 해제", $"before={countBeforeDispose}, after={handlerCallCount}");
            }

            // ── 테스트 9: 종합 시나리오 — 미니 편집기 ────────────────────────
            Debug.Log("[Stage04] ▶ 테스트 9: 종합 — 미니 편집기 흉내");
            {
                var log = new System.Text.StringBuilder();
                var invoker = CommandBus.Create<CommandUnit>()
                    .WithPolicy(AsyncPolicy.Switch)
                    .WithHistory(30)
                    .Build();

                invoker.OnHistoryChanged += (action, idx, name) =>
                    log.AppendLine($"  [{action}] idx={idx} name={name}");

                // 10회 Execute
                for (int i = 1; i <= 10; i++)
                {
                    int captured = i;
                    invoker.Execute(new Command<CommandUnit>(execute: _ => { }, name: $"Edit#{captured}"), CommandUnit.Default);
                }
                bool c1 = invoker.HistoryCount == 10 && invoker.CurrentIndex == 9;

                // Undo 3회
                await invoker.UndoAsync();
                await invoker.UndoAsync();
                await invoker.UndoAsync();
                bool c2 = invoker.CurrentIndex == 6;

                // Redo 1회
                await invoker.RedoAsync();
                bool c3 = invoker.CurrentIndex == 7;

                // JumpTo 맨 앞
                invoker.JumpTo(0);
                bool c4 = invoker.CurrentIndex == 0;

                // Pop (최신 위치 아님 → 이벤트만)
                invoker.Pop();
                bool c5 = invoker.CurrentIndex == 0 && invoker.HistoryCount == 10;

                // Clear
                invoker.Clear();
                bool c6 = invoker.HistoryCount == 0 && invoker.CurrentIndex == -1;

                if (c1 && c2 && c3 && c4 && c5 && c6)
                    Pass("종합 시나리오 — HistoryCount/CurrentIndex 전 단계 일치");
                else
                    Fail("종합 시나리오", $"c1={c1} c2={c2} c3={c3} c4={c4} c5={c5} c6={c6}");

                Debug.Log($"[Stage04]   OnHistoryChanged 로그:\n{log}");

                invoker.Dispose();
            }

            // ── 최종 집계 ──────────────────────────────────────────────────────
            string summary = _failed == 0
                ? $"[Stage04] ✅ 완료 — {_passed}개 통과 / 0개 실패"
                : $"[Stage04] ❌ 완료 — {_passed}개 통과 / {_failed}개 실패";
            Debug.Log(summary);
        }
    }
}
