#if ENABLE_HYBRIDCLR
using System;
using System.Collections.Generic;
using System.Reflection;
using AlicizaX;
using UnityEngine;
using YooAsset;
using HybridCLR;

namespace Unity.Startup.Procedure
{
    public sealed class ProcedureLoadAssembly : ProcedureBase
    {
        private int m_LoadAssetCount;
        private int m_LoadMetadataAssetCount;
        private bool m_LoadAssemblyComplete;
        private bool m_LoadMetadataAssemblyComplete;
        private bool m_LoadAssemblyWait;
        private bool m_LoadMetadataAssemblyWait;
        private Assembly m_MainLogicAssembly;
        private List<Assembly> m_HotfixAssemblys;

        protected override void OnEnter()
        {
            Log.Info(" ProcedureLoadAssembly OnEnter");
            m_LoadAssemblyComplete = false;
            m_HotfixAssemblys = new List<Assembly>();

            //AOT Assembly加载原始metadata
            if (GameApp.Resource.PlayMode == EPlayMode.EditorSimulateMode)
            {
                m_LoadMetadataAssemblyComplete = true;
            }
            else
            {
                m_LoadMetadataAssemblyComplete = false;
                LoadMetadataForAOTAssembly();
            }

            if (GameApp.Resource.PlayMode == EPlayMode.EditorSimulateMode)
            {
                m_MainLogicAssembly = GetMainLogicAssembly();
            }
            else
            {
                foreach (string hotUpdateDllName in StartupSetting.HotUpdateAssemblies)
                {
                    m_LoadAssetCount++;
                    GameApp.Resource.LoadAsset<TextAsset>(hotUpdateDllName, LoadAssetSuccess);
                }

                m_LoadAssemblyWait = true;
            }


            if (m_LoadAssetCount == 0)
            {
                m_LoadAssemblyComplete = true;
            }
        }

        protected override void OnUpdate()
        {
            if (!m_LoadAssemblyComplete)
            {
                return;
            }

            if (!m_LoadMetadataAssemblyComplete)
            {
                return;
            }

            AllAssemblyLoadComplete();
        }

        private void AllAssemblyLoadComplete()
        {
            SwitchProcedure<ProcedureUpdateFinishState>();

            if (m_MainLogicAssembly == null)
            {
                Log.Warning($"Main logic assembly missing.");
                return;
            }

            var appType = m_MainLogicAssembly.GetType(StartupSetting.EntranceClass);
            if (appType == null)
            {
                Log.Warning($"Main logic type '{StartupSetting.EntranceClass}' missing.");
                return;
            }

            var entryMethod = appType.GetMethod(StartupSetting.EntranceMethod);
            if (entryMethod == null)
            {
                Log.Warning($"Main logic entry method '{StartupSetting.EntranceMethod}' missing.");
                return;
            }

            object[] objects = new object[] { new object[] { m_HotfixAssemblys } };
            entryMethod.Invoke(appType, objects);
        }

        private Assembly GetMainLogicAssembly()
        {
            m_HotfixAssemblys.Clear();
            Assembly mainLogicAssembly = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Compare(StartupSetting.EntranceDll, $"{assembly.GetName().Name}.dll",
                        StringComparison.Ordinal) == 0)
                {
                    mainLogicAssembly = assembly;
                }

                foreach (var hotUpdateDllName in StartupSetting.HotUpdateAssemblies)
                {
                    if (hotUpdateDllName == $"{assembly.GetName().Name}.dll" && !m_HotfixAssemblys.Contains(assembly))
                    {
                        m_HotfixAssemblys.Add(assembly);
                    }
                }

                if (mainLogicAssembly != null && m_HotfixAssemblys.Count ==
                    StartupSetting.HotUpdateAssemblies.Count)
                {
                    break;
                }
            }

            return mainLogicAssembly;
        }

        /// <summary>
        /// 加载代码资源成功回调。
        /// </summary>
        /// <param name="textAsset">代码资产。</param>
        private void LoadAssetSuccess(TextAsset textAsset)
        {
            m_LoadAssetCount--;
            if (textAsset == null)
            {
                Log.Warning($"Load Assembly failed.");
                return;
            }

            var assetName = textAsset.name;
            Log.Info($"LoadAssetSuccess, assetName: [ {assetName} ]");

            try
            {
                var assembly = Assembly.Load(textAsset.bytes);
                if (string.Compare(StartupSetting.EntranceDll, assetName, StringComparison.Ordinal) == 0)
                {
                    m_MainLogicAssembly = assembly;
                }

                m_HotfixAssemblys.Add(assembly);
                Log.Info($"Assembly [ {assembly.GetName().Name} ] loaded");
            }
            catch (Exception e)
            {
                Log.Error(e.Message);
                throw;
            }
            finally
            {
                m_LoadAssemblyComplete = m_LoadAssemblyWait && 0 == m_LoadAssetCount;
            }

            GameApp.Resource.UnloadAsset(textAsset);
        }

        /// <summary>
        /// 为Aot Assembly加载原始metadata， 这个代码放Aot或者热更新都行。
        /// 一旦加载后，如果AOT泛型函数对应native实现不存在，则自动替换为解释模式执行。
        /// </summary>
        public void LoadMetadataForAOTAssembly()
        {
            // 可以加载任意aot assembly的对应的dll。但要求dll必须与unity build过程中生成的裁剪后的dll一致，而不能直接使用原始dll。
            // 我们在BuildProcessor_xxx里添加了处理代码，这些裁剪后的dll在打包时自动被复制到 {项目目录}/HybridCLRData/AssembliesPostIl2CppStrip/{Target} 目录。

            // 注意，补充元数据是给AOT dll补充元数据，而不是给热更新dll补充元数据。
            // 热更新dll不缺元数据，不需要补充，如果调用LoadMetadataForAOTAssembly会返回错误
            if (AOTGenericReferences.PatchedAOTAssemblyList.Count == 0)
            {
                m_LoadMetadataAssemblyComplete = true;
                return;
            }

            foreach (string aotDllName in AOTGenericReferences.PatchedAOTAssemblyList)
            {
                m_LoadMetadataAssetCount++;
                GameApp.Resource.LoadAsset<TextAsset>(aotDllName, LoadMetadataAssetSuccess);
            }

            m_LoadMetadataAssemblyWait = true;
        }

        /// <summary>
        /// 加载元数据资源成功回调。
        /// </summary>
        /// <param name="textAsset">代码资产。</param>
        private unsafe void LoadMetadataAssetSuccess(TextAsset textAsset)
        {
            m_LoadMetadataAssetCount--;
            if (null == textAsset)
            {
                Log.Info($"LoadMetadataAssetSuccess:Load Metadata failed.");
                return;
            }

            string assetName = textAsset.name;
            try
            {
                byte[] dllBytes = textAsset.bytes;
                fixed (byte* ptr = dllBytes)
                {
                    // 加载assembly对应的dll，会自动为它hook。一旦Aot泛型函数的native函数不存在，用解释器版本代码
                    HomologousImageMode mode = HomologousImageMode.SuperSet;
                    LoadImageErrorCode err =
                        (LoadImageErrorCode)RuntimeApi.LoadMetadataForAOTAssembly(dllBytes, mode);
                    Log.Info($"LoadMetadataForAOTAssembly:{assetName}. mode:{mode} ret:{err}");
                }
            }
            catch (Exception e)
            {
                Log.Error(e.Message);
                throw;
            }
            finally
            {
                m_LoadMetadataAssemblyComplete = m_LoadMetadataAssemblyWait && 0 == m_LoadMetadataAssetCount;
            }

            GameApp.Resource.UnloadAsset(textAsset);
        }
    }
}

#endif
