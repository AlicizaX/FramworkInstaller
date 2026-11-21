using AlicizaX;
using UnityEngine;

namespace Unity.Startup.Procedure
{
    // 启动流程对外发布的事件。模板不带启动界面，需要时自己写界面订阅即可：
    //   流程 → 界面：LaunchStateChangedEvent、LaunchDownloadProgressEvent

    internal enum LaunchState
    {
        /// <summary>正在检查 / 更新。</summary>
        Checking,

        /// <summary>更新完成。</summary>
        Ready
    }

    internal readonly struct LaunchStateChangedEvent : IPayloadEventArgs
    {
        public readonly LaunchState State;

        public LaunchStateChangedEvent(LaunchState state) => State = state;
    }

    internal readonly struct LaunchDownloadProgressEvent : IPayloadEventArgs
    {
        public readonly int CurrentCount;
        public readonly int TotalCount;
        public readonly long CurrentBytes;
        public readonly long TotalBytes;

        public LaunchDownloadProgressEvent(int currentCount, int totalCount, long currentBytes, long totalBytes)
        {
            CurrentCount = currentCount;
            TotalCount = totalCount;
            CurrentBytes = currentBytes;
            TotalBytes = totalBytes;
        }

        /// <summary>0~1，有字节数时按字节计算，否则按文件数。</summary>
        public float Progress => Mathf.Clamp01(TotalBytes > 0
            ? (float)((double)CurrentBytes / TotalBytes)
            : CurrentCount / (float)Mathf.Max(1, TotalCount));
    }
}
