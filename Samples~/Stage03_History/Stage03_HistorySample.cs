using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Cysharp.Threading.Tasks;
using UniTaskCommandBus;

namespace UniTaskCommandBus.Samples
{
    public class Stage03_HistorySample : MonoBehaviour
    {
        // ── Named command for snapshot tests ────────────────────────────────

        private class MutableNameCommand : CommandBase<CommandUnit>
        {
            public string CurrentName; // mutable — used to verify snapshot behaviour
            public override string Name => CurrentName;
            public override void Execute(CommandUnit _) { }
        }

        // ── Entry point ──────────────────────────────────────────────────────

        private int _passed;
        private int _failed;

        private void Start() => RunAsync().Forget();

        private async UniTaskVoid RunAsync()
        {
            _passed = 0;
            _failed = 0;

            await Test01_BasicUndoRedo();
            await Test02_BranchingDiscard();
            await Test03_Pop();
            await Test04_EmptyState();
            await Test05_FullHistoryExecute();
            await Test06_OnHistoryChangedLog();
            await Test07_GetNameSnapshot();
            await Test08_UndoRedoBoundary();
            await Test09_PopEmptyException();
            await Test10_AsyncUndoRedoSwitch();
            await Test11_DropNotRecorded();
            await Test12_ThrottleLastMiddleNotRecorded();
            await Test13_CancelAllQueueNotRecorded();
            await Test14_EventBeforeLambda();

            string summary = _failed == 0
                ? $"[Stage03] ✅ 완료 — {_passed}개 통과 / 0개 실패"
                : $"[Stage03] ❌ 완료 — {_passed}개 통과 / {_failed}개 실패";
            Debug.Log(summary);
        }

        private void Pass(string label)
        {
            _passed++;
            Debug.Log($"[Stage03]   ✅ {label} 통과");
        }

        private void Fail(string label, string detail)
        {
            _failed++;
            Debug.Log($"[Stage03]   ❌ [실패] {label}: {detail}");
        }

        // ── Test 01: 기본 Execute → Undo → Redo ─────────────────────────────

        private async UniTask Test01_BasicUndoRedo()
        {
            Debug.Log("[Stage03] ▶ 테스트 1: 기본 Execute → Undo → Redo");

            var invoker = CommandBus.Create<CommandUnit>().WithHistory(5).Build();
            int val = 0;

            Command<CommandUnit> Cmd(int add, string n) =>
                new Command<CommandUnit>(_ => val += add, _ => val -= add, n);

            invoker.Execute(Cmd(1, "A"));
            invoker.Execute(Cmd(10, "B"));
            invoker.Execute(Cmd(100, "C"));
            // val=111, count=3, index=2

            bool step1 = invoker.HistoryCount == 3 && invoker.CurrentIndex == 2 && val == 111;

            await invoker.UndoAsync(); // undoes C: val=11, index=1
            await invoker.UndoAsync(); // undoes B: val=1,  index=0

            bool step2 = invoker.CurrentIndex == 0 && val == 1;

            await invoker.RedoAsync(); // redoes B: val=11, index=1

            bool step3 = invoker.CurrentIndex == 1 && val == 11;

            Debug.Log($"[Stage03]   count={invoker.HistoryCount} idx={invoker.CurrentIndex} val={val}");

            if (step1 && step2 && step3) Pass("테스트1");
            else Fail("테스트1", $"step1={step1} step2={step2} step3={step3} val={val} idx={invoker.CurrentIndex}");

            invoker.Dispose();
        }

        // ── Test 02: 분기 — 중간에서 새 Execute ──────────────────────────────

        private async UniTask Test02_BranchingDiscard()
        {
            Debug.Log("[Stage03] ▶ 테스트 2: Execute 후 중간에서 새 Execute (분기 폐기)");

            var invoker = CommandBus.Create<CommandUnit>().WithHistory(10).Build();
            int val = 0;

            Command<CommandUnit> Cmd(int v, string n) =>
                new Command<CommandUnit>(_ => val += v, _ => val -= v, n);

            for (int i = 1; i <= 5; i++) invoker.Execute(Cmd(1, $"cmd{i}"));
            // count=5, index=4

            await invoker.UndoAsync();
            await invoker.UndoAsync();
            // index=2

            invoker.Execute(Cmd(99, "newCmd"));
            // entries after index 2 are discarded, new entry added → count=4, index=3

            bool pass = invoker.HistoryCount == 4 && invoker.CurrentIndex == 3;
            Debug.Log($"[Stage03]   count={invoker.HistoryCount} idx={invoker.CurrentIndex}");

            if (pass) Pass("테스트2");
            else Fail("테스트2", $"count={invoker.HistoryCount} idx={invoker.CurrentIndex}");

            invoker.Dispose();
        }

        // ── Test 03: Pop ─────────────────────────────────────────────────────

        private async UniTask Test03_Pop()
        {
            Debug.Log("[Stage03] ▶ 테스트 3: Pop — 최신 위치 / 탐색 중");

            var invoker = CommandBus.Create<CommandUnit>().WithHistory(10).Build();
            int val = 0;
            Command<CommandUnit> Cmd(string n) => new Command<CommandUnit>(_ => val++, _ => val--, n);

            invoker.Execute(Cmd("A"));
            invoker.Execute(Cmd("B"));
            invoker.Execute(Cmd("C"));
            // count=3, index=2

            // Pop at latest
            invoker.Pop();
            bool atLatest = invoker.HistoryCount == 2 && invoker.CurrentIndex == 1;
            Debug.Log($"[Stage03]   Pop@latest → count={invoker.HistoryCount} idx={invoker.CurrentIndex}");

            // Setup for browsing Pop
            invoker.Execute(Cmd("D"));
            invoker.Execute(Cmd("E"));
            // count=4, index=3
            await invoker.UndoAsync();
            // index=2 (browsing)

            int countBefore = invoker.HistoryCount;
            int idxBefore = invoker.CurrentIndex;

            HistoryActionType? popAction = null;
            int popIdx = -99;
            invoker.OnHistoryChanged += (a, i, _) => { if (a == HistoryActionType.Pop) { popAction = a; popIdx = i; } };

            invoker.Pop(); // browsing → no history change, only event

            bool browsing = invoker.HistoryCount == countBefore
                         && invoker.CurrentIndex == idxBefore
                         && popAction == HistoryActionType.Pop
                         && popIdx == idxBefore;

            Debug.Log($"[Stage03]   Pop@browsing → count={invoker.HistoryCount} idx={invoker.CurrentIndex} eventIdx={popIdx}");

            if (atLatest && browsing) Pass("테스트3");
            else Fail("테스트3", $"atLatest={atLatest} browsing={browsing}");

            invoker.Dispose();
        }

        // ── Test 04: 빈 히스토리 상태 ────────────────────────────────────────

        private UniTask Test04_EmptyState()
        {
            Debug.Log("[Stage03] ▶ 테스트 4: 빈 히스토리 초기 상태 / Clear 후");

            var invoker = CommandBus.Create<CommandUnit>().WithHistory(5).Build();

            bool emptyOnCreate = invoker.HistoryCount == 0 && invoker.CurrentIndex == -1;

            invoker.Execute(new Command<CommandUnit>(_ => { }, name: "X"));
            invoker.Clear();

            bool emptyAfterClear = invoker.HistoryCount == 0 && invoker.CurrentIndex == -1;

            Debug.Log($"[Stage03]   onCreate={emptyOnCreate} afterClear={emptyAfterClear}");

            if (emptyOnCreate && emptyAfterClear) Pass("테스트4");
            else Fail("테스트4", $"onCreate={emptyOnCreate} afterClear={emptyAfterClear}");

            invoker.Dispose();
            return UniTask.CompletedTask;
        }

        // ── Test 05: 꽉 찬 상태에서 Execute ─────────────────────────────────

        private UniTask Test05_FullHistoryExecute()
        {
            Debug.Log("[Stage03] ▶ 테스트 5: 꽉 찬 상태에서 Execute — 맨 앞 제거");

            var invoker = CommandBus.Create<CommandUnit>().WithHistory(3).Build();

            invoker.Execute(new Command<CommandUnit>(_ => { }, name: "A"));
            invoker.Execute(new Command<CommandUnit>(_ => { }, name: "B"));
            invoker.Execute(new Command<CommandUnit>(_ => { }, name: "C"));
            // Full: [A, B, C], index=2

            invoker.Execute(new Command<CommandUnit>(_ => { }, name: "D"));
            // A removed, [B, C, D], index=2

            bool pass = invoker.HistoryCount == 3
                     && invoker.CurrentIndex == 2
                     && invoker.GetName(0) == "B"
                     && invoker.GetName(2) == "D";

            Debug.Log($"[Stage03]   count={invoker.HistoryCount} idx={invoker.CurrentIndex} name[0]={invoker.GetName(0)} name[2]={invoker.GetName(2)}");

            if (pass) Pass("테스트5");
            else Fail("테스트5", $"count={invoker.HistoryCount} idx={invoker.CurrentIndex} name0={invoker.GetName(0)}");

            invoker.Dispose();
            return UniTask.CompletedTask;
        }

        // ── Test 06: OnHistoryChanged 이벤트 로그 ────────────────────────────

        private async UniTask Test06_OnHistoryChangedLog()
        {
            Debug.Log("[Stage03] ▶ 테스트 6: OnHistoryChanged 이벤트 로그");

            var invoker = CommandBus.Create<CommandUnit>().WithHistory(5).Build();
            var events = new List<(HistoryActionType a, int i, string n)>();
            invoker.OnHistoryChanged += (a, i, n) =>
            {
                events.Add((a, i, n));
                Debug.Log($"[Stage03]   event: {a} idx={i} name='{n}'");
            };

            invoker.Execute(new Command<CommandUnit>(_ => { }, name: "cmd1"));
            await invoker.UndoAsync();
            await invoker.RedoAsync();
            invoker.Pop();
            invoker.Clear();

            bool pass = events.Count == 5
                     && events[0].a == HistoryActionType.Execute
                     && events[1].a == HistoryActionType.Undo
                     && events[2].a == HistoryActionType.Redo
                     && events[3].a == HistoryActionType.Pop
                     && events[4].a == HistoryActionType.Clear;

            Debug.Log($"[Stage03]   eventCount={events.Count}");

            if (pass) Pass("테스트6");
            else Fail("테스트6", $"eventCount={events.Count} types={string.Join(",", events.ConvertAll(e => e.a.ToString()))}");

            invoker.Dispose();
        }

        // ── Test 07: GetName 스냅샷 검증 ──────────────────────────────────────

        private UniTask Test07_GetNameSnapshot()
        {
            Debug.Log("[Stage03] ▶ 테스트 7: GetName 스냅샷 — 적재 시점 이름 고정");

            var invoker = CommandBus.Create<CommandUnit>().WithHistory(5).Build();

            // Lambda commands with distinct names
            invoker.Execute(new Command<CommandUnit>(_ => { }, name: "alpha"));
            invoker.Execute(new Command<CommandUnit>(_ => { }, name: "beta"));
            invoker.Execute(new Command<CommandUnit>(_ => { }, name: "gamma"));

            bool lambdaPass = invoker.GetName(0) == "alpha"
                           && invoker.GetName(1) == "beta"
                           && invoker.GetName(2) == "gamma";

            // Mutable class command: name changes after Execute — snapshot must be frozen
            var mutableCmd = new MutableNameCommand { CurrentName = "before" };
            invoker.Execute(mutableCmd, CommandUnit.Default);
            string snapshotted = invoker.GetName(3); // should be "before"
            mutableCmd.CurrentName = "after"; // mutate after execute
            string afterMutate = invoker.GetName(3); // should still be "before"

            bool snapshotPass = snapshotted == "before" && afterMutate == "before";

            Debug.Log($"[Stage03]   lambda={lambdaPass} snapshot='{snapshotted}'→'{afterMutate}'");

            if (lambdaPass && snapshotPass) Pass("테스트7");
            else Fail("테스트7", $"lambda={lambdaPass} snapshot={snapshotPass} snapshotted='{snapshotted}' after='{afterMutate}'");

            invoker.Dispose();
            return UniTask.CompletedTask;
        }

        // ── Test 08: Undo/Redo 경계 예외 ─────────────────────────────────────

        private UniTask Test08_UndoRedoBoundary()
        {
            Debug.Log("[Stage03] ▶ 테스트 8: Undo/Redo 경계 예외");

            var invoker = CommandBus.Create<CommandUnit>().WithHistory(5).Build();

            bool undoEmpty = false;
            try { invoker.Undo(); }
            catch (InvalidOperationException) { undoEmpty = true; }

            invoker.Execute(new Command<CommandUnit>(_ => { }));
            // index=0, at latest

            bool redoAtEnd = false;
            try { invoker.Redo(); }
            catch (InvalidOperationException) { redoAtEnd = true; }

            Debug.Log($"[Stage03]   undoEmpty={undoEmpty} redoAtEnd={redoAtEnd}");

            if (undoEmpty && redoAtEnd) Pass("테스트8");
            else Fail("테스트8", $"undoEmpty={undoEmpty} redoAtEnd={redoAtEnd}");

            invoker.Dispose();
            return UniTask.CompletedTask;
        }

        // ── Test 09: Pop 빈 상태 예외 ─────────────────────────────────────────

        private UniTask Test09_PopEmptyException()
        {
            Debug.Log("[Stage03] ▶ 테스트 9: Pop 빈 히스토리 예외");

            var invoker = CommandBus.Create<CommandUnit>().WithHistory(5).Build();

            bool threw = false;
            try { invoker.Pop(); }
            catch (InvalidOperationException) { threw = true; }

            Debug.Log($"[Stage03]   threw={threw}");

            if (threw) Pass("테스트9");
            else Fail("테스트9", "expected InvalidOperationException, none thrown");

            invoker.Dispose();
            return UniTask.CompletedTask;
        }

        // ── Test 10: 비동기 Undo/Redo Switch 동작 ────────────────────────────

        private async UniTask Test10_AsyncUndoRedoSwitch()
        {
            Debug.Log("[Stage03] ▶ 테스트 10: 비동기 Undo/Redo 연타 — Switch 동작");

            var invoker = CommandBus.Create<CommandUnit>().WithHistory(5).Build();
            int undoCount = 0;

            var asyncCmd = new AsyncCommand<CommandUnit>(async (_, ct) =>
            {
                await UniTask.Delay(500, cancellationToken: ct);
            }, undo: async (_, ct) =>
            {
                await UniTask.Delay(300, cancellationToken: ct);
                undoCount++;
            });

            await invoker.ExecuteAsync(asyncCmd);
            await invoker.ExecuteAsync(asyncCmd);
            await invoker.ExecuteAsync(asyncCmd);
            // index=2

            // Rapid Undo x3 — first two should be Cancelled (Switch), last Completed
            var t1 = invoker.UndoAsync();
            var t2 = invoker.UndoAsync();
            var t3 = invoker.UndoAsync();

            var r1 = await t1;
            var r2 = await t2;
            var r3 = await t3;

            Debug.Log($"[Stage03]   undo1={r1} undo2={r2} undo3={r3} undoCount={undoCount} idx={invoker.CurrentIndex}");

            bool pass = r1 == ExecutionResult.Cancelled
                     && r2 == ExecutionResult.Cancelled
                     && r3 == ExecutionResult.Completed
                     && invoker.CurrentIndex == -1;

            if (pass) Pass("테스트10");
            else Fail("테스트10", $"r1={r1} r2={r2} r3={r3} idx={invoker.CurrentIndex}");

            invoker.Dispose();
        }

        // ── Test 11: Drop 거부 — 히스토리 미적재 ─────────────────────────────

        private async UniTask Test11_DropNotRecorded()
        {
            Debug.Log("[Stage03] ▶ 테스트 11: Drop 거부된 커맨드는 히스토리에 안 남는다");

            var invoker = CommandBus.Create<CommandUnit>()
                .WithPolicy(AsyncPolicy.Drop)
                .WithHistory(5)
                .Build();

            int eventCount = 0;
            invoker.OnHistoryChanged += (_, _, _) => eventCount++;

            var longCmd = new AsyncCommand<CommandUnit>(async (_, ct) =>
                await UniTask.Delay(2000, cancellationToken: ct), name: "long");
            var shortCmd = new AsyncCommand<CommandUnit>(async (_, ct) =>
                await UniTask.Delay(100, cancellationToken: ct), name: "short");

            var t1 = invoker.ExecuteAsync(longCmd);
            await UniTask.Delay(300);
            var r2 = await invoker.ExecuteAsync(shortCmd); // should be Dropped

            invoker.Cancel();
            await t1;

            bool pass = r2 == ExecutionResult.Dropped
                     && invoker.HistoryCount == 1
                     && eventCount == 1;

            Debug.Log($"[Stage03]   r2={r2} histCount={invoker.HistoryCount} events={eventCount}");

            if (pass) Pass("테스트11");
            else Fail("테스트11", $"r2={r2} histCount={invoker.HistoryCount} events={eventCount}");

            invoker.Dispose();
        }

        // ── Test 12: ThrottleLast 중간값 미적재 ──────────────────────────────

        private async UniTask Test12_ThrottleLastMiddleNotRecorded()
        {
            Debug.Log("[Stage03] ▶ 테스트 12: ThrottleLast 중간값은 히스토리에 안 남는다");

            var invoker = CommandBus.Create<CommandUnit>()
                .WithPolicy(AsyncPolicy.ThrottleLast)
                .WithHistory(5)
                .Build();

            var first = new AsyncCommand<CommandUnit>(async (_, ct) =>
                await UniTask.Delay(800, cancellationToken: ct), name: "first");
            var cmdB = new AsyncCommand<CommandUnit>(async (_, ct) =>
                await UniTask.Delay(100, cancellationToken: ct), name: "B");
            var cmdC = new AsyncCommand<CommandUnit>(async (_, ct) =>
                await UniTask.Delay(100, cancellationToken: ct), name: "C");
            var cmdD = new AsyncCommand<CommandUnit>(async (_, ct) =>
                await UniTask.Delay(100, cancellationToken: ct), name: "D");

            var tFirst = invoker.ExecuteAsync(first);
            await UniTask.Delay(100);
            var tB = invoker.ExecuteAsync(cmdB);
            var tC = invoker.ExecuteAsync(cmdC);
            var tD = invoker.ExecuteAsync(cmdD);

            var rFirst = await tFirst;
            var rB = await tB;
            var rC = await tC;
            var rD = await tD;

            bool pass = rFirst == ExecutionResult.Completed
                     && rB == ExecutionResult.Dropped
                     && rC == ExecutionResult.Dropped
                     && rD == ExecutionResult.Completed
                     && invoker.HistoryCount == 2
                     && invoker.GetName(0) == "first"
                     && invoker.GetName(1) == "D";

            Debug.Log($"[Stage03]   first={rFirst} B={rB} C={rC} D={rD} histCount={invoker.HistoryCount}");

            if (pass) Pass("테스트12");
            else Fail("테스트12", $"histCount={invoker.HistoryCount} B={rB} C={rC} D={rD}");

            invoker.Dispose();
        }

        // ── Test 13: CancelAll 대기 항목 미적재 ──────────────────────────────

        private async UniTask Test13_CancelAllQueueNotRecorded()
        {
            Debug.Log("[Stage03] ▶ 테스트 13: Sequential CancelAll — 큐 대기 항목 미적재");

            var invoker = CommandBus.Create<CommandUnit>()
                .WithPolicy(AsyncPolicy.Sequential)
                .WithHistory(5)
                .Build();

            var cmd1 = new AsyncCommand<CommandUnit>(async (_, ct) =>
                await UniTask.Delay(2000, cancellationToken: ct), name: "first");
            var cmd2 = new AsyncCommand<CommandUnit>(async (_, ct) =>
                await UniTask.Delay(100, cancellationToken: ct), name: "queued2");
            var cmd3 = new AsyncCommand<CommandUnit>(async (_, ct) =>
                await UniTask.Delay(100, cancellationToken: ct), name: "queued3");

            var t1 = invoker.ExecuteAsync(cmd1); // starts → recorded
            var t2 = invoker.ExecuteAsync(cmd2); // queued → NOT yet recorded
            var t3 = invoker.ExecuteAsync(cmd3); // queued → NOT yet recorded

            await UniTask.Delay(200);
            invoker.CancelAll();

            var r1 = await t1;
            var r2 = await t2;
            var r3 = await t3;

            bool pass = r1 == ExecutionResult.Cancelled
                     && r2 == ExecutionResult.Cancelled
                     && r3 == ExecutionResult.Cancelled
                     && invoker.HistoryCount == 1; // only first was started

            Debug.Log($"[Stage03]   r1={r1} r2={r2} r3={r3} histCount={invoker.HistoryCount}");

            if (pass) Pass("테스트13");
            else Fail("테스트13", $"r1={r1} r2={r2} r3={r3} histCount={invoker.HistoryCount}");

            invoker.Dispose();
        }

        // ── Test 14: 이벤트가 람다보다 먼저 발행 ─────────────────────────────

        private UniTask Test14_EventBeforeLambda()
        {
            Debug.Log("[Stage03] ▶ 테스트 14: 이벤트가 람다보다 먼저 발행 (selfPop 검증)");

            var invoker = CommandBus.Create<CommandUnit>().WithHistory(10).Build();
            var events = new List<(HistoryActionType a, int i, string n)>();
            invoker.OnHistoryChanged += (a, i, n) => events.Add((a, i, n));

            // Execute two baseline commands
            invoker.Execute(new Command<CommandUnit>(_ => { }, name: "A"));
            invoker.Execute(new Command<CommandUnit>(_ => { }, name: "B"));
            events.Clear();

            // selfPop: lambda immediately calls invoker.Pop()
            var selfPopCmd = new Command<CommandUnit>(
                execute: _ => invoker.Pop(),
                undo: _ => { },
                name: "selfPop");

            invoker.Execute(selfPopCmd);

            // Expected:
            // events[0] = (Execute, 2, "selfPop")  ← fired BEFORE lambda
            // events[1] = (Pop,     1, "B")         ← fired from within lambda

            bool evt0ok = events.Count >= 1
                       && events[0].a == HistoryActionType.Execute
                       && events[0].i == 2
                       && events[0].n == "selfPop";

            bool evt1ok = events.Count >= 2
                       && events[1].a == HistoryActionType.Pop
                       && events[1].i == 1;

            bool histOk = invoker.HistoryCount == 2 && invoker.CurrentIndex == 1;

            Debug.Log($"[Stage03]   events={events.Count} evt0=({events[0].a},{events[0].i},'{events[0].n}')");
            if (events.Count >= 2) Debug.Log($"[Stage03]   evt1=({events[1].a},{events[1].i})");
            Debug.Log($"[Stage03]   histCount={invoker.HistoryCount} idx={invoker.CurrentIndex}");

            if (evt0ok && evt1ok && histOk) Pass("테스트14");
            else Fail("테스트14", $"evt0={evt0ok} evt1={evt1ok} hist={histOk}");

            invoker.Dispose();
            return UniTask.CompletedTask;
        }
    }
}
