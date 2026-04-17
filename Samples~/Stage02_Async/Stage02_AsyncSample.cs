using System.Threading;
using UnityEngine;
using Cysharp.Threading.Tasks;
using UniTaskCommandBus;

namespace UniTaskCommandBus.Samples
{
    public class Stage02_AsyncSample : MonoBehaviour
    {
        // ── Inner types ──────────────────────────────────────────────────────

        private class DelayLogCommand : AsyncCommandBase<CommandUnit>
        {
            private readonly int _ms;
            private readonly string _label;
            public override string Name => $"DelayLog({_label})";

            public DelayLogCommand(int ms, string label)
            {
                _ms = ms;
                _label = label;
            }

            public override async UniTask ExecuteAsync(CommandUnit _, CancellationToken ct)
            {
                await UniTask.Delay(_ms, cancellationToken: ct);
                Debug.Log($"[Stage02]   DelayLogCommand({_label}) 완료 ({_ms}ms)");
            }
        }

        // ── Entry point ──────────────────────────────────────────────────────

        private int _passed;
        private int _failed;

        private void Start() => RunAsync().Forget();

        private async UniTaskVoid RunAsync()
        {
            _passed = 0;
            _failed = 0;

            await Test01_AsyncBasic();
            await Test02_Cancel();
            await Test03_AsyncCommandBase();
            await Test04_Drop();
            await Test05_Sequential();
            await Test06_Switch();
            await Test07_ThrottleLast();
            await Test08_Parallel();
            await Test09_CancelAll();
            await Test10_SyncVsAsync();

            string summary = _failed == 0
                ? $"[Stage02] ✅ 완료 — {_passed}개 통과 / 0개 실패"
                : $"[Stage02] ❌ 완료 — {_passed}개 통과 / {_failed}개 실패";
            Debug.Log(summary);
        }

        private void Pass(string label)
        {
            _passed++;
            Debug.Log($"[Stage02]   ✅ {label} 통과");
        }

        private void Fail(string label, string detail)
        {
            _failed++;
            Debug.Log($"[Stage02]   ❌ [실패] {label}: {detail}");
        }

        // ── Test 01: AsyncCommand 기본 ────────────────────────────────────────

        private async UniTask Test01_AsyncBasic()
        {
            Debug.Log("[Stage02] ▶ 테스트 1: AsyncCommand 람다 기본 (500ms 대기)");

            var invoker = CommandBus.Create<CommandUnit>()
                .WithPolicy(AsyncPolicy.Sequential)
                .Build();

            bool done = false;
            var cmd = new AsyncCommand<CommandUnit>(async (_, ct) =>
            {
                await UniTask.Delay(500, cancellationToken: ct);
                done = true;
            });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await invoker.ExecuteAsync(cmd);
            sw.Stop();

            Debug.Log($"[Stage02]   result={result}, done={done}, elapsed={sw.ElapsedMilliseconds}ms");

            if (result == ExecutionResult.Completed && done && sw.ElapsedMilliseconds >= 450)
                Pass("테스트1");
            else
                Fail("테스트1", $"result={result} done={done} elapsed={sw.ElapsedMilliseconds}ms");

            invoker.Dispose();
        }

        // ── Test 02: Cancel ───────────────────────────────────────────────────

        private async UniTask Test02_Cancel()
        {
            Debug.Log("[Stage02] ▶ 테스트 2: Cancel — 진행 중 커맨드 취소");

            var invoker = CommandBus.Create<CommandUnit>()
                .WithPolicy(AsyncPolicy.Sequential)
                .Build();

            var cmd = new AsyncCommand<CommandUnit>(async (_, ct) =>
                await UniTask.Delay(3000, cancellationToken: ct));

            var task = invoker.ExecuteAsync(cmd);

            await UniTask.Delay(500);
            invoker.Cancel();

            var result = await task;
            Debug.Log($"[Stage02]   result={result}");

            if (result == ExecutionResult.Cancelled) Pass("테스트2");
            else Fail("테스트2", $"expected Cancelled, actual={result}");

            invoker.Dispose();
        }

        // ── Test 03: AsyncCommandBase 클래스 ─────────────────────────────────

        private async UniTask Test03_AsyncCommandBase()
        {
            Debug.Log("[Stage02] ▶ 테스트 3: AsyncCommandBase 클래스 커맨드");

            var invoker = CommandBus.Create<CommandUnit>()
                .WithPolicy(AsyncPolicy.Sequential)
                .Build();

            var cmd = new DelayLogCommand(200, "테스트3");
            var result = await invoker.ExecuteAsync(cmd, CommandUnit.Default);

            Debug.Log($"[Stage02]   result={result}");

            if (result == ExecutionResult.Completed) Pass("테스트3");
            else Fail("테스트3", $"expected Completed, actual={result}");

            invoker.Dispose();
        }

        // ── Test 04: Drop 정책 ────────────────────────────────────────────────

        private async UniTask Test04_Drop()
        {
            Debug.Log("[Stage02] ▶ 테스트 4: Drop 정책 — 진행 중이면 두 번째는 Dropped");

            var invoker = CommandBus.Create<CommandUnit>()
                .WithPolicy(AsyncPolicy.Drop)
                .Build();

            var cmd1 = new AsyncCommand<CommandUnit>(async (_, ct) =>
                await UniTask.Delay(2000, cancellationToken: ct));
            var cmd2 = new AsyncCommand<CommandUnit>(async (_, ct) =>
                await UniTask.Delay(100, cancellationToken: ct));

            var t1 = invoker.ExecuteAsync(cmd1);
            await UniTask.Delay(300);
            var r2 = await invoker.ExecuteAsync(cmd2);

            Debug.Log($"[Stage02]   두 번째 result={r2}");

            invoker.Cancel();
            await t1;

            if (r2 == ExecutionResult.Dropped) Pass("테스트4");
            else Fail("테스트4", $"expected Dropped, actual={r2}");

            invoker.Dispose();
        }

        // ── Test 05: Sequential 정책 ─────────────────────────────────────────

        private async UniTask Test05_Sequential()
        {
            Debug.Log("[Stage02] ▶ 테스트 5: Sequential 정책 — 순서대로 실행");

            var invoker = CommandBus.Create<CommandUnit>()
                .WithPolicy(AsyncPolicy.Sequential)
                .Build();

            int order = 0;
            bool seq1 = false, seq2 = false, seq3 = false;

            async UniTask MakeCmd(int id, System.Action<bool> setFlag)
            {
                var cmd = new AsyncCommand<CommandUnit>(async (_, ct) =>
                {
                    await UniTask.Delay(300, cancellationToken: ct);
                    Debug.Log($"[Stage02]   Sequential cmd{id} 완료 (order={++order})");
                    setFlag(true);
                });
                await invoker.ExecuteAsync(cmd);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await UniTask.WhenAll(MakeCmd(1, v => seq1 = v), MakeCmd(2, v => seq2 = v), MakeCmd(3, v => seq3 = v));
            sw.Stop();

            Debug.Log($"[Stage02]   elapsed={sw.ElapsedMilliseconds}ms order={order}");

            if (seq1 && seq2 && seq3 && sw.ElapsedMilliseconds >= 850 && order == 3)
                Pass("테스트5");
            else
                Fail("테스트5", $"seq={seq1},{seq2},{seq3} order={order} elapsed={sw.ElapsedMilliseconds}ms");

            invoker.Dispose();
        }

        // ── Test 06: Switch 정책 ─────────────────────────────────────────────

        private async UniTask Test06_Switch()
        {
            Debug.Log("[Stage02] ▶ 테스트 6: Switch 정책 — 새 커맨드가 이전 것을 취소");

            var invoker = CommandBus.Create<CommandUnit>()
                .WithPolicy(AsyncPolicy.Switch)
                .Build();

            var cmd1 = new AsyncCommand<CommandUnit>(async (_, ct) =>
                await UniTask.Delay(1000, cancellationToken: ct));
            var cmd2 = new AsyncCommand<CommandUnit>(async (_, ct) =>
                await UniTask.Delay(300, cancellationToken: ct));

            var t1 = invoker.ExecuteAsync(cmd1);
            await UniTask.Delay(200);
            var t2 = invoker.ExecuteAsync(cmd2);

            var r1 = await t1;
            var r2 = await t2;

            Debug.Log($"[Stage02]   r1={r1} r2={r2}");

            if (r1 == ExecutionResult.Cancelled && r2 == ExecutionResult.Completed)
                Pass("테스트6");
            else
                Fail("테스트6", $"r1={r1} r2={r2}");

            invoker.Dispose();
        }

        // ── Test 07: ThrottleLast 정책 ───────────────────────────────────────

        private async UniTask Test07_ThrottleLast()
        {
            Debug.Log("[Stage02] ▶ 테스트 7: ThrottleLast — 중간값 Dropped, 마지막만 실행");

            var invoker = CommandBus.Create<CommandUnit>()
                .WithPolicy(AsyncPolicy.ThrottleLast)
                .Build();

            var first = new AsyncCommand<CommandUnit>(async (_, ct) =>
                await UniTask.Delay(1000, cancellationToken: ct));
            var cmdB = new AsyncCommand<CommandUnit>(async (_, ct) => await UniTask.Delay(200, cancellationToken: ct), name: "B");
            var cmdC = new AsyncCommand<CommandUnit>(async (_, ct) => await UniTask.Delay(200, cancellationToken: ct), name: "C");
            var cmdD = new AsyncCommand<CommandUnit>(async (_, ct) =>
            {
                await UniTask.Delay(200, cancellationToken: ct);
                Debug.Log("[Stage02]   D 완료");
            }, name: "D");

            var tFirst = invoker.ExecuteAsync(first);
            await UniTask.Delay(100);
            var tB = invoker.ExecuteAsync(cmdB);
            var tC = invoker.ExecuteAsync(cmdC);
            var tD = invoker.ExecuteAsync(cmdD);

            var rFirst = await tFirst;
            var rB = await tB;
            var rC = await tC;
            var rD = await tD;

            Debug.Log($"[Stage02]   first={rFirst} B={rB} C={rC} D={rD}");

            if (rFirst == ExecutionResult.Completed && rB == ExecutionResult.Dropped
                && rC == ExecutionResult.Dropped && rD == ExecutionResult.Completed)
                Pass("테스트7");
            else
                Fail("테스트7", $"first={rFirst} B={rB} C={rC} D={rD}");

            invoker.Dispose();
        }

        // ── Test 08: Parallel 정책 ────────────────────────────────────────────

        private async UniTask Test08_Parallel()
        {
            Debug.Log("[Stage02] ▶ 테스트 8: Parallel 정책 — 동시 실행 ~500ms");

            var invoker = CommandBus.Create<CommandUnit>()
                .WithPolicy(AsyncPolicy.Parallel)
                .Build();

            int completedCount = 0;

            AsyncCommand<CommandUnit> MakeParallelCmd(int id) =>
                new AsyncCommand<CommandUnit>(async (_, ct) =>
                {
                    await UniTask.Delay(500, cancellationToken: ct);
                    completedCount++;
                    Debug.Log($"[Stage02]   Parallel cmd{id} 완료");
                });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await UniTask.WhenAll(
                invoker.ExecuteAsync(MakeParallelCmd(1)),
                invoker.ExecuteAsync(MakeParallelCmd(2)),
                invoker.ExecuteAsync(MakeParallelCmd(3)));
            sw.Stop();

            Debug.Log($"[Stage02]   elapsed={sw.ElapsedMilliseconds}ms completedCount={completedCount}");

            if (completedCount == 3 && sw.ElapsedMilliseconds < 900)
                Pass("테스트8");
            else
                Fail("테스트8", $"count={completedCount} elapsed={sw.ElapsedMilliseconds}ms");

            invoker.Dispose();
        }

        // ── Test 09: CancelAll ────────────────────────────────────────────────

        private async UniTask Test09_CancelAll()
        {
            Debug.Log("[Stage02] ▶ 테스트 9: CancelAll — 큐의 모든 커맨드 취소");

            var invoker = CommandBus.Create<CommandUnit>()
                .WithPolicy(AsyncPolicy.Sequential)
                .Build();

            AsyncCommand<CommandUnit> MakeCmd(string label) =>
                new AsyncCommand<CommandUnit>(async (_, ct) =>
                {
                    Debug.Log($"[Stage02]   {label} 시작");
                    await UniTask.Delay(2000, cancellationToken: ct);
                    Debug.Log($"[Stage02]   {label} 완료");
                });

            var t1 = invoker.ExecuteAsync(MakeCmd("cmd1"));
            var t2 = invoker.ExecuteAsync(MakeCmd("cmd2"));
            var t3 = invoker.ExecuteAsync(MakeCmd("cmd3"));

            await UniTask.Delay(200);
            invoker.CancelAll();

            var r1 = await t1;
            var r2 = await t2;
            var r3 = await t3;

            Debug.Log($"[Stage02]   r1={r1} r2={r2} r3={r3}");

            if (r1 == ExecutionResult.Cancelled && r2 == ExecutionResult.Cancelled && r3 == ExecutionResult.Cancelled)
                Pass("테스트9");
            else
                Fail("테스트9", $"r1={r1} r2={r2} r3={r3}");

            invoker.Dispose();
        }

        // ── Test 10: 동기 커맨드 ExecuteAsync vs Execute ─────────────────────

        private async UniTask Test10_SyncVsAsync()
        {
            Debug.Log("[Stage02] ▶ 테스트 10: 동기 커맨드를 ExecuteAsync / Execute 각각으로 호출");

            var invoker = CommandBus.Create<CommandUnit>()
                .WithPolicy(AsyncPolicy.Sequential)
                .Build();

            int syncCounter = 0;
            var syncCmd = new Command<CommandUnit>(_ => syncCounter++);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r1 = await invoker.ExecuteAsync(syncCmd);
            sw.Stop();

            var r2 = invoker.Execute(syncCmd);

            Debug.Log($"[Stage02]   ExecuteAsync result={r1} elapsed={sw.ElapsedMilliseconds}ms");
            Debug.Log($"[Stage02]   Execute result={r2} counter={syncCounter}");

            if (r1 == ExecutionResult.Completed && r2 == ExecutionResult.Completed
                && syncCounter == 2 && sw.ElapsedMilliseconds < 100)
                Pass("테스트10");
            else
                Fail("테스트10", $"r1={r1} r2={r2} counter={syncCounter} elapsed={sw.ElapsedMilliseconds}ms");

            invoker.Dispose();
        }
    }
}
