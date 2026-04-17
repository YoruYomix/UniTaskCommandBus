using System;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UniTaskCommandBus.Samples
{
    /// <summary>
    /// Stage 07 — Fault Tolerance
    /// Tests that ExecutionResult.Faulted is returned when a command lambda throws,
    /// that policy flags (_isRunning / _isLoopRunning) are reset via finally,
    /// and that Sequential / ThrottleLast loops continue after a faulted entry.
    ///
    /// NOTE: Drop / Switch / Parallel tests will show an exception stack trace in the
    /// Unity Console. This is EXPECTED — exceptions from those policies bubble through
    /// UniTask's Forget path to Unity's unhandled-exception handler.
    /// Sequential / ThrottleLast exceptions are caught within the loop runner and will
    /// NOT appear in the Console (they are reported as Faulted via ExecutionResult only).
    /// </summary>
    public class Stage07_FaultToleranceSample : MonoBehaviour
    {
        private int _passed;
        private int _failed;

        private void Pass(string label)
        {
            _passed++;
            Debug.Log($"[Stage07]   ✅ {label} 통과");
        }

        private void Fail(string label, string detail)
        {
            _failed++;
            Debug.Log($"[Stage07]   ❌ [실패] {label}: {detail}");
        }

        private void Start() => RunAsync().Forget();

        private async UniTaskVoid RunAsync()
        {
            Debug.Log("[Stage07] ▶ Fault Tolerance 테스트 시작");

            await Test1_FaultedResult_Switch();
            await Test2_Drop_FlagResetAfterFault();
            await Test3_Sequential_FlagResetAfterFault();
            await Test4_ThrottleLast_FlagResetAfterFault();
            await Test5_Sequential_QueueContinuesAfterFault();
            await Test6_ThrottleLast_SlotRunsAfterFault();
            await Test7_HistoryInvoker_HistoryPreservedAfterFault();
            await Test8_Parallel_FaultIsolation();
            await Test9_OnError_EventFires();

            string icon = _failed == 0 ? "✅" : "❌";
            Debug.Log($"[Stage07] {icon} 완료 — {_passed}개 통과 / {_failed}개 실패");
        }

        // ── Test 1 ───────────────────────────────────────────────────────────

        private async UniTask Test1_FaultedResult_Switch()
        {
            Debug.Log("[Stage07] ▶ 테스트 1: Switch 정책 — 람다 throw 시 ExecuteAsync가 Faulted 반환");
            var invoker = CommandBus.Create<CommandUnit>().WithPolicy(AsyncPolicy.Switch).Build();

            var cmd = new AsyncCommand<CommandUnit>(async (_, ct) =>
            {
                await UniTask.Yield(ct);
                throw new InvalidOperationException("의도적 예외");
            });

            var result = await invoker.ExecuteAsync(cmd);

            if (result == ExecutionResult.Faulted)
                Pass("테스트1 Faulted 반환");
            else
                Fail("테스트1 Faulted 반환", $"expected=Faulted, actual={result}");

            invoker.Dispose();
        }

        // ── Test 2 ───────────────────────────────────────────────────────────

        private async UniTask Test2_Drop_FlagResetAfterFault()
        {
            Debug.Log("[Stage07] ▶ 테스트 2: Drop 정책 — 예외 후 _isRunning finally 리셋, 다음 Execute 수락");
            var invoker = CommandBus.Create<CommandUnit>().WithPolicy(AsyncPolicy.Drop).Build();

            var faultCmd = new AsyncCommand<CommandUnit>(async (_, ct) =>
            {
                await UniTask.Yield(ct);
                throw new InvalidOperationException("fault");
            });

            var r1 = await invoker.ExecuteAsync(faultCmd);

            bool accepted = false;
            var r2 = invoker.Execute(_ => { accepted = true; });

            if (r1 == ExecutionResult.Faulted && r2 != ExecutionResult.Dropped && accepted)
                Pass("테스트2 Drop 플래그 리셋");
            else
                Fail("테스트2 Drop 플래그 리셋", $"r1={r1}, r2={r2}, accepted={accepted}");

            invoker.Dispose();
        }

        // ── Test 3 ───────────────────────────────────────────────────────────

        private async UniTask Test3_Sequential_FlagResetAfterFault()
        {
            Debug.Log("[Stage07] ▶ 테스트 3: Sequential 정책 — 예외 후 _isLoopRunning finally 리셋, 다음 Execute 수락");
            var invoker = CommandBus.Create<CommandUnit>().WithPolicy(AsyncPolicy.Sequential).Build();

            var faultCmd = new AsyncCommand<CommandUnit>(async (_, ct) =>
            {
                await UniTask.Yield(ct);
                throw new InvalidOperationException("fault");
            });

            var r1 = await invoker.ExecuteAsync(faultCmd);

            bool accepted = false;
            var r2 = await invoker.ExecuteAsync(new Command<CommandUnit>(_ => { accepted = true; }));

            if (r1 == ExecutionResult.Faulted && r2 == ExecutionResult.Completed && accepted)
                Pass("테스트3 Sequential 플래그 리셋");
            else
                Fail("테스트3 Sequential 플래그 리셋", $"r1={r1}, r2={r2}, accepted={accepted}");

            invoker.Dispose();
        }

        // ── Test 4 ───────────────────────────────────────────────────────────

        private async UniTask Test4_ThrottleLast_FlagResetAfterFault()
        {
            Debug.Log("[Stage07] ▶ 테스트 4: ThrottleLast 정책 — 예외 후 _isRunning finally 리셋, 다음 Execute 수락");
            var invoker = CommandBus.Create<CommandUnit>().WithPolicy(AsyncPolicy.ThrottleLast).Build();

            var faultCmd = new AsyncCommand<CommandUnit>(async (_, ct) =>
            {
                await UniTask.Yield(ct);
                throw new InvalidOperationException("fault");
            });

            var r1 = await invoker.ExecuteAsync(faultCmd);

            bool accepted = false;
            var r2 = await invoker.ExecuteAsync(new Command<CommandUnit>(_ => { accepted = true; }));

            if (r1 == ExecutionResult.Faulted && r2 == ExecutionResult.Completed && accepted)
                Pass("테스트4 ThrottleLast 플래그 리셋");
            else
                Fail("테스트4 ThrottleLast 플래그 리셋", $"r1={r1}, r2={r2}, accepted={accepted}");

            invoker.Dispose();
        }

        // ── Test 5 ───────────────────────────────────────────────────────────

        private async UniTask Test5_Sequential_QueueContinuesAfterFault()
        {
            Debug.Log("[Stage07] ▶ 테스트 5: Sequential 큐 — 첫 항목 예외 후 대기 항목 계속 실행");
            var invoker = CommandBus.Create<CommandUnit>().WithPolicy(AsyncPolicy.Sequential).Build();

            var faultCmd = new AsyncCommand<CommandUnit>(async (_, ct) =>
            {
                await UniTask.Yield(ct);
                throw new InvalidOperationException("fault");
            });

            bool secondRan = false;
            bool thirdRan = false;

            var t1 = invoker.ExecuteAsync(faultCmd);
            var t2 = invoker.ExecuteAsync(new Command<CommandUnit>(_ => { secondRan = true; }));
            var t3 = invoker.ExecuteAsync(new Command<CommandUnit>(_ => { thirdRan = true; }));

            var (r1, r2, r3) = await UniTask.WhenAll(t1, t2, t3);

            if (r1 == ExecutionResult.Faulted
                && r2 == ExecutionResult.Completed && secondRan
                && r3 == ExecutionResult.Completed && thirdRan)
                Pass("테스트5 Sequential 큐 계속 실행");
            else
                Fail("테스트5 Sequential 큐 계속 실행",
                    $"r1={r1}, r2={r2}(ran={secondRan}), r3={r3}(ran={thirdRan})");

            invoker.Dispose();
        }

        // ── Test 6 ───────────────────────────────────────────────────────────

        private async UniTask Test6_ThrottleLast_SlotRunsAfterFault()
        {
            Debug.Log("[Stage07] ▶ 테스트 6: ThrottleLast 슬롯 — 첫 항목 예외 후 슬롯 항목 실행");
            var invoker = CommandBus.Create<CommandUnit>().WithPolicy(AsyncPolicy.ThrottleLast).Build();

            var faultCmd = new AsyncCommand<CommandUnit>(async (_, ct) =>
            {
                await UniTask.Yield(ct);
                throw new InvalidOperationException("fault");
            });

            bool slotRan = false;
            var slotCmd = new AsyncCommand<CommandUnit>(async (_, ct) =>
            {
                await UniTask.Yield(ct);
                slotRan = true;
            });

            var t1 = invoker.ExecuteAsync(faultCmd);  // 즉시 실행 시작
            var t2 = invoker.ExecuteAsync(slotCmd);   // 슬롯 대기

            var (r1, r2) = await UniTask.WhenAll(t1, t2);

            if (r1 == ExecutionResult.Faulted && r2 == ExecutionResult.Completed && slotRan)
                Pass("테스트6 ThrottleLast 슬롯 계속 실행");
            else
                Fail("테스트6 ThrottleLast 슬롯 계속 실행",
                    $"r1={r1}, r2={r2}(ran={slotRan})");

            invoker.Dispose();
        }

        // ── Test 7 ───────────────────────────────────────────────────────────

        private async UniTask Test7_HistoryInvoker_HistoryPreservedAfterFault()
        {
            Debug.Log("[Stage07] ▶ 테스트 7: HistoryInvoker — 예외 후에도 히스토리 항목 유지 (적재는 람다 실행 전)");
            var invoker = CommandBus.Create<CommandUnit>()
                .WithPolicy(AsyncPolicy.Switch)
                .WithHistory(10)
                .Build();

            var faultCmd = new AsyncCommand<CommandUnit>(
                execute: async (_, ct) =>
                {
                    await UniTask.Yield(ct);
                    throw new InvalidOperationException("fault");
                },
                name: "FaultCmd"
            );

            var result = await invoker.ExecuteAsync(faultCmd);

            bool nameOk = invoker.HistoryCount == 1 && invoker.GetName(0) == "FaultCmd";

            if (result == ExecutionResult.Faulted
                && invoker.HistoryCount == 1
                && invoker.CurrentIndex == 0
                && nameOk)
                Pass("테스트7 히스토리 유지");
            else
                Fail("테스트7 히스토리 유지",
                    $"result={result}, historyCount={invoker.HistoryCount}, " +
                    $"currentIndex={invoker.CurrentIndex}, name={invoker.GetName(0)}");

            invoker.Dispose();
        }

        // ── Test 8 ───────────────────────────────────────────────────────────

        private async UniTask Test8_Parallel_FaultIsolation()
        {
            Debug.Log("[Stage07] ▶ 테스트 8: Parallel 정책 — 한 커맨드 예외가 다른 커맨드에 영향 없음");
            var invoker = CommandBus.Create<CommandUnit>().WithPolicy(AsyncPolicy.Parallel).Build();

            bool aRan = false;
            bool bRan = false;

            var cmdA = new AsyncCommand<CommandUnit>(async (_, ct) =>
            {
                await UniTask.Delay(50, cancellationToken: ct);
                aRan = true;
            });
            var cmdFault = new AsyncCommand<CommandUnit>(async (_, ct) =>
            {
                await UniTask.Yield(ct);
                throw new InvalidOperationException("fault");
            });
            var cmdB = new AsyncCommand<CommandUnit>(async (_, ct) =>
            {
                await UniTask.Delay(50, cancellationToken: ct);
                bRan = true;
            });

            var tA = invoker.ExecuteAsync(cmdA);
            var tFault = invoker.ExecuteAsync(cmdFault);
            var tB = invoker.ExecuteAsync(cmdB);

            var (rA, rFault, rB) = await UniTask.WhenAll(tA, tFault, tB);

            if (rA == ExecutionResult.Completed && aRan
                && rFault == ExecutionResult.Faulted
                && rB == ExecutionResult.Completed && bRan)
                Pass("테스트8 Parallel 격리");
            else
                Fail("테스트8 Parallel 격리",
                    $"A={rA}(ran={aRan}), Fault={rFault}, B={rB}(ran={bRan})");

            invoker.Dispose();
        }

        // ── Test 9 ───────────────────────────────────────────────────────────

        private async UniTask Test9_OnError_EventFires()
        {
            Debug.Log("[Stage07] ▶ 테스트 9: OnError 이벤트 — 람다 throw 시 예외 인스턴스 전달 확인");
            var invoker = CommandBus.Create<CommandUnit>().WithPolicy(AsyncPolicy.Switch).Build();

            Exception captured = null;
            invoker.OnError += ex => { captured = ex; };

            var expected = new InvalidOperationException("OnError 테스트 예외");
            var cmd = new AsyncCommand<CommandUnit>(async (_, ct) =>
            {
                await UniTask.Yield(ct);
                throw expected;
            });

            var result = await invoker.ExecuteAsync(cmd);

            if (result == ExecutionResult.Faulted
                && ReferenceEquals(captured, expected))
                Pass("테스트9 OnError 이벤트");
            else
                Fail("테스트9 OnError 이벤트",
                    $"result={result}, captured={captured?.Message ?? "null"}");

            invoker.Dispose();
        }
    }
}
