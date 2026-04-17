using UnityEngine;
using UniTaskCommandBus;

namespace UniTaskCommandBus.Samples
{
    public class Stage01_BasicsSample : MonoBehaviour
    {
        private record MoveArg(int Delta);

        private class LogCommand : CommandBase<CommandUnit>
        {
            public override void Execute(CommandUnit _)
            {
                Debug.Log("[Stage01]   LogCommand.Execute() called");
            }

            public override void Undo(CommandUnit _)
            {
                Debug.Log("[Stage01]   LogCommand.Undo() called");
            }
        }

        private class NamedMoveCommand : CommandBase<MoveArg>
        {
            private readonly int _delta;
            public override string Name => $"Move by {_delta}";

            public NamedMoveCommand(int delta) => _delta = delta;

            public override void Execute(MoveArg a) { }
            public override void Undo(MoveArg a) { }
        }

        private int _passed;
        private int _failed;

        private void Start()
        {
            _passed = 0;
            _failed = 0;

            Test1_LambdaCommandUnit();
            Test2_LambdaWithPayload();
            Test3_ShortcutLambda();
            Test4_ClassCommand();
            Test5_NameProperty();

            string summary = _failed == 0
                ? $"[Stage01] ✅ 완료 — {_passed}개 통과 / 0개 실패"
                : $"[Stage01] ❌ 완료 — {_passed}개 통과 / {_failed}개 실패";
            Debug.Log(summary);
        }

        private void Pass(string label)
        {
            _passed++;
            Debug.Log($"[Stage01]   ✅ {label} 통과");
        }

        private void Fail(string label, string detail)
        {
            _failed++;
            Debug.Log($"[Stage01]   ❌ [실패] {label}: {detail}");
        }

        private void Test1_LambdaCommandUnit()
        {
            Debug.Log("[Stage01] ▶ 테스트 1: 람다 커맨드 Execute (CommandUnit)");

            var invoker = CommandBus.Create<CommandUnit>().Build();
            int counter = 0;

            Debug.Log($"[Stage01]   before: counter={counter}");
            invoker.Execute(execute: _ => counter++, undo: _ => counter--);
            Debug.Log($"[Stage01]   after execute: counter={counter}");

            if (counter == 1) Pass("테스트1");
            else Fail("테스트1", $"expected counter=1, actual={counter}");
        }

        private void Test2_LambdaWithPayload()
        {
            Debug.Log("[Stage01] ▶ 테스트 2: 람다 커맨드 + payload record");

            var invoker = CommandBus.Create<MoveArg>().Build();
            int pos = 0;

            var cmd = new Command<MoveArg>(
                execute: m => pos += m.Delta,
                undo: m => pos -= m.Delta
            );

            invoker.Execute(cmd, new MoveArg(5));
            Debug.Log($"[Stage01]   after execute(5): pos={pos}");

            if (pos == 5) Pass("테스트2");
            else Fail("테스트2", $"expected pos=5, actual={pos}");
        }

        private void Test3_ShortcutLambda()
        {
            Debug.Log("[Stage01] ▶ 테스트 3: 숏컷 메서드로 람다 직접 넘기기");

            var invoker = CommandBus.Create<CommandUnit>().Build();
            int value = 0;

            invoker.Execute(execute: _ => value = 42, undo: _ => value = 0);
            Debug.Log($"[Stage01]   after shortcut execute: value={value}");

            if (value == 42) Pass("테스트3");
            else Fail("테스트3", $"expected value=42, actual={value}");
        }

        private void Test4_ClassCommand()
        {
            Debug.Log("[Stage01] ▶ 테스트 4: 클래스 커맨드 (CommandBase<T>)");

            var invoker = CommandBus.Create<CommandUnit>().Build();
            var result = invoker.Execute(new LogCommand(), CommandUnit.Default);
            Debug.Log($"[Stage01]   ExecutionResult={result}");

            if (result == ExecutionResult.Completed) Pass("테스트4");
            else Fail("테스트4", $"expected Completed, actual={result}");
        }

        private void Test5_NameProperty()
        {
            Debug.Log("[Stage01] ▶ 테스트 5: Name 확인 — 람다와 클래스 각각");

            int delta = 10;
            var lambdaCmd = new Command<MoveArg>(
                execute: m => { },
                undo: m => { },
                name: $"Move by {delta}"
            );
            string expectedLambdaName = $"Move by {delta}";
            bool lambdaPass = lambdaCmd.Name == expectedLambdaName;
            Debug.Log($"[Stage01]   lambda cmd.Name='{lambdaCmd.Name}' expected='{expectedLambdaName}'");

            var classCmd = new NamedMoveCommand(delta);
            string expectedClassName = $"Move by {delta}";
            bool classPass = classCmd.Name == expectedClassName;
            Debug.Log($"[Stage01]   class cmd.Name='{classCmd.Name}' expected='{expectedClassName}'");

            if (lambdaPass && classPass) Pass("테스트5");
            else Fail("테스트5", $"lambda={lambdaPass} class={classPass}");
        }
    }
}
