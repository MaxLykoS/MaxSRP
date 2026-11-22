using System;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public abstract class MaxRenderPass : IDisposable
    {
        // 只回收已销毁相机的资源。停用或暂时缺席的相机仍保留缓存。
        public virtual void PruneDestroyedCameras() { }

        public void Dispose() => ReleaseResources();

        protected abstract void ReleaseResources();

        // Pass 决定何时提交命令，作用域负责把缓冲区归还给池。
        protected readonly ref struct PooledCommandBuffer
        {
            public CommandBuffer Buffer { get; }

            public PooledCommandBuffer(string name) => Buffer = CommandBufferPool.Get(name);

            public void Dispose() => CommandBufferPool.Release(Buffer);
        }
    }

    // Keep the GPU marker open across commands enqueued directly on the render context.
    internal ref struct GpuPassScope
    {
        private readonly ScriptableRenderContext m_context;
        private readonly string m_name;
        private bool m_started;

        public CommandBuffer Buffer { get; }

        public GpuPassScope(ScriptableRenderContext context, string name)
        {
            m_context = context;
            m_name = name;
            m_started = false;
            Buffer = CommandBufferPool.Get();
            Buffer.BeginSample(name);
        }

        public void Enqueue()
        {
            m_context.ExecuteCommandBuffer(Buffer);
            m_started = true;
            Buffer.Clear();
        }

        public void Dispose()
        {
            try
            {
                if (m_started)
                {
                    // Drop any unfinished recording before queuing the closing marker.
                    Buffer.Clear();
                    Buffer.EndSample(m_name);
                    m_context.ExecuteCommandBuffer(Buffer);
                }
            }
            finally
            {
                CommandBufferPool.Release(Buffer);
            }
        }
    }
}
