using AlicizaX;

namespace Unity.Startup.Procedure
{
    /// <summary>
    /// 更新完成：销毁启动流程并调用热更入口。
    /// </summary>
    internal sealed class ProcedureEnterGameState : ProcedureBase
    {
        private readonly LaunchContext _context;

        public ProcedureEnterGameState(LaunchContext context) => _context = context;

        protected override void OnEnter()
        {
            EventBus.Publish(new LaunchStateChangedEvent(LaunchState.Ready));
            ProcedureBuilder.DestroyProcedure();
            // 热更入口约定：static void Entrance(object[] args)，args[0] 为 List<Assembly>。
            _context.EntryMethod?.Invoke(null, new object[] { new object[] { _context.HotfixAssemblies } });
        }
    }
}
