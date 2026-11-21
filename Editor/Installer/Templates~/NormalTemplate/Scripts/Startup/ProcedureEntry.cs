using AlicizaX;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YooAsset;

namespace Unity.Startup.Procedure
{
    /// <summary>
    /// 启动入口（挂在 Main 场景）：等 YooAsset 初始化完成后组装启动流程。
    /// <para>CheckVersion → InitPackage → DownloadBundle → ClearCache → LoadAssembly → EnterGame</para>
    /// <para>流程之间不互相引用，只通过 <see cref="LaunchContext"/> 交换数据；
    /// 需要启动界面时，订阅 <c>Event/LaunchEvents.cs</c> 中的事件即可。</para>
    /// </summary>
    public sealed class ProcedureEntry : MonoBehaviour
    {
        private async UniTaskVoid Start()
        {
            EventBus.Publish(new LaunchStateChangedEvent(LaunchState.Checking));
            await UniTask.WaitUntil(() => YooAssets.IsInitialized);

            var context = new LaunchContext();
            ProcedureBuilder.InitializeProcedure(new ProcedureBase[]
            {
                new ProcedureCheckVersionState(context),
                new ProcedureInitPackageState(context),
                new ProcedureDownloadBundleState(),
                new ProcedureClearCacheState(),
                new ProcedureLoadAssemblyState(context),
                new ProcedureEnterGameState(context),
            }, typeof(ProcedureCheckVersionState));

            Destroy(gameObject);
        }
    }
}
