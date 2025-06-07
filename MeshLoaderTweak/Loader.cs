using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using Elements.Core;
using HarmonyLib;
using ResoniteModLoader;
using FrooxEngine;
using UnityFrooxEngineRunner;

namespace MeshLoadTweak
{
    public class Loader : ResoniteMod
    {
        public static ModConfiguration config;

        public override string Name => "MeshLoadSequencer";
        public override string Author => "bd_";
        public override string Version => "0.0.1";
        public override string Link => "https://github.com/bdunderscore/MeshLoaderTweak";
        private static FieldInfo UnityAssetIntegrator_stopwatch;
        private static Type t_QueueAction;
        private static FieldInfo f_QueueAction_action;
        private static FieldInfo f_QueueAction_actionWithData;
        private static FieldInfo f_QueueAction_coroutine;
        private static FieldInfo f_QueueAction_data;
        private static FieldInfo f_highpriorityQueue;
        private static FieldInfo f_processingQueue;
        private static MethodInfo m_spinQ_TryDequeue;
        private static FieldInfo f_AssetConnector_Asset;
    
        public override void OnEngineInit()
        {
            //UniLog.FlushEveryMessage = true;

            Engine.Current.RunPostInit(Init);
        }

        Type FindQueueAction()
        {
            foreach (var ty in typeof(UnityAssetIntegrator).Assembly.GetTypes())
            {
                if (ty.Name.EndsWith("QueueAction") && ty.FullName.Contains("UnityAssetIntegrator"))
                {
                    return ty;
                }
            }

            return null;
        }
        
        private void Init() {
            UniLog.Log("Initializing MeshLoadSequencer...");
            
            try
            {
                var uai = typeof(UnityAssetIntegrator);
                UniLog.Log("UAI: " + uai);
                UnityAssetIntegrator_stopwatch = AccessTools.Field(uai, "stopwatch");
                UniLog.Log("SW: " + UnityAssetIntegrator_stopwatch);
                t_QueueAction = FindQueueAction();
                UniLog.Log("QueueAction: " + t_QueueAction);
                f_QueueAction_action = AccessTools.Field(t_QueueAction, "action");
                UniLog.Log("QueueAction.action: " + f_QueueAction_action);
                f_QueueAction_actionWithData = AccessTools.Field(t_QueueAction, "actionWithData");
                UniLog.Log("QueueAction.actionWithData: " + f_QueueAction_actionWithData);
                f_QueueAction_coroutine = AccessTools.Field(t_QueueAction, "coroutine");
                UniLog.Log("QueueAction.coroutine: " + f_QueueAction_coroutine);
                f_QueueAction_data = AccessTools.Field(t_QueueAction, "data");
                UniLog.Log("QueueAction.data: " + f_QueueAction_data);
                f_highpriorityQueue = AccessTools.Field(uai, "highpriorityQueue");
                UniLog.Log("f_highpriorityQueue: " + f_highpriorityQueue);
                f_processingQueue = AccessTools.Field(uai, "processingQueue");
                UniLog.Log("f_processingQueue: " + f_processingQueue);
                m_spinQ_TryDequeue = AccessTools.Method(f_highpriorityQueue.FieldType, "TryDequeue");
                UniLog.Log("m_spinQ_TryDequeue: " + m_spinQ_TryDequeue);
                f_AssetConnector_Asset = AccessTools.Field(typeof(AssetConnector), "asset");
                
            }
            catch (Exception e)
            {
                UniLog.Error("Failed to set up reflection: " + e);
                return;
            }

            config = GetConfiguration();
            Harmony harmony = new Harmony("nadena.dev.MeshLoadSequencer");
            harmony.PatchAll();
        
            UniLog.Log("f_QueueAction_data != null: " + (f_QueueAction_data != null));
            UniLog.Log("f_highpriorityQueue != null: " + (f_highpriorityQueue != null));
            UniLog.Log("f_processingQueue != null: " + (f_processingQueue != null));
            UniLog.Log("m_spinQ_TryDequeue != null: " + (m_spinQ_TryDequeue != null));
            
            UniLog.Log("test!");
        }

        [HarmonyPatch(typeof(MeshConnector), "UpdateMeshData")]
        class PatchUpdateMeshData
        {
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (var insn in instructions)
                {
                    UniLog.Log("MeshConnector.UpdateMeshData: " + insn);

                    yield return insn;
                    if (insn.opcode == OpCodes.Call && ((MethodInfo)insn.operand).Name == "Upload")
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(
                            OpCodes.Call,
                            AccessTools.Method(typeof(PatchUpdateMeshData), nameof(WrapEnumerator))
                        );
                    }
                }
            }

            private static IEnumerator WrapEnumerator(IEnumerator inner, MeshConnector connector)
            {
                return new ComplexityWrapper(inner, ComputeMeshComplexity(
                    (Mesh) f_AssetConnector_Asset.GetValue(connector)));
            }

            private static int ComputeMeshComplexity(Mesh connectorMesh)
            {
                var meshx = connectorMesh.Data;

                int complexity = 0;

                if (meshx.BoneCount > 0) complexity++;
                foreach (var shape in meshx.BlendShapes)
                {
                    complexity += 1 + shape.FrameCount;
                }

                return complexity;
            }
        }

        class ComplexityWrapper : IEnumerator
        {
            private readonly IEnumerator _inner;
            public readonly int complexity;
            
            public ComplexityWrapper(IEnumerator inner, int complexity)
            {
                _inner = inner;
                this.complexity = complexity;
            }
            
            public bool MoveNext()
            {
                return _inner.MoveNext();
            }

            public void Reset()
            {
                _inner.Reset();
            }

            public object Current => _inner.Current;
        }

        [HarmonyPatch(typeof(UnityAssetIntegrator), "ProcessQueue", new[] { typeof(double), typeof(bool) })]
        class PatchProcessQueue
        {
            private static readonly Queue<IEnumerator> _deferredQueue = new();
            
            // We allow multiple coroutines to run in parallel, timeslicing between them.
            // These enumerators simply cycle through the _activeJobs queue until they complete.
            // To avoid this getting out of hand, we limit the number of items in the queue.
            // We'll only enqueue new jobs from the high-priority integration queue if we're
            // under the hp_inProgressLimit, and only from the normal queue once below lp_inProgressLimit.
            //
            // To avoid particularly heavy meshes (eg avatars) blocking the queue, we also measure the complexity
            // of the mesh (more-or-less, the number of yields it'll take to upload, which is basically the number of
            // blendshapes), and set an even lower limit for those - we only have up to HEAVY_LIMIT
            // of those jobs in the active jobs buffer at a time. If we encounter these jobs when we already
            // have HEAVY_LIMIT jobs in the queue, we push them off to the _deferredQueue. Note that we lose the
            // high priority integration flag for blendshape heavy mesh assets here.
            private static readonly Queue<IEnumerator> _activeJobs = new();
            private static readonly object[] _tryDeQbuf = new object[1];
            private static bool _triggered;
            private static int _activeHeavy;
            
            private const int HEAVY_THRESHOLD = 10;
            
            
            private const int hp_inProgressLimit = 16;
            private const int lp_inProgressLimit = 8;
            private const int heavy_inProgressLimit = 4;

            private static bool IsHeavy(IEnumerator operation)
            {
                return operation is ComplexityWrapper c && c.complexity > HEAVY_THRESHOLD;
            }
            
            public static bool Prefix(UnityAssetIntegrator __instance, double maxMilliseconds, bool renderThread, ref int __result)
            {
                // Use normal codepath if we're on the render thread
                if (renderThread) return true;

                int workCount = 0;
                
                var stopwatch = (Stopwatch) UnityAssetIntegrator_stopwatch.GetValue(__instance);
                stopwatch.Restart();

                var hpq = f_highpriorityQueue.GetValue(__instance);
                var lpq = f_processingQueue.GetValue(__instance);
                
                double elapsed;
                do
                {
                    elapsed = stopwatch.GetElapsedMilliseconds();

                    bool hasNewWork = false;
                    if (_activeJobs.Count < hp_inProgressLimit && (bool)m_spinQ_TryDequeue.Invoke(hpq, _tryDeQbuf))
                    {
                        hasNewWork = true;
                    } else if (_activeHeavy < heavy_inProgressLimit && _deferredQueue.Count > 0) {
                        _activeJobs.Enqueue(_deferredQueue.Dequeue());
                        _activeHeavy++;
                    } else if (_activeJobs.Count < lp_inProgressLimit && (bool)m_spinQ_TryDequeue.Invoke(lpq, _tryDeQbuf)) {
                        hasNewWork = true;
                    }

                    if (hasNewWork || _activeJobs.Count > 0)
                    {
                        workCount++;
                    }

                    try
                    {
                        if (hasNewWork)
                        {
                            var action = (Action)f_QueueAction_action.GetValue(_tryDeQbuf[0]);
                            if (action != null)
                            {
                                action();
                                continue;
                            }

                            var actionWithData = (Action<object>)f_QueueAction_actionWithData.GetValue(_tryDeQbuf[0]);
                            if (actionWithData != null)
                            {
                                var data = f_QueueAction_data.GetValue(_tryDeQbuf[0]);
                                actionWithData(data);
                                continue;
                            }

                            var coro = (IEnumerator)f_QueueAction_coroutine.GetValue(_tryDeQbuf[0]);

                            if (IsHeavy(coro))
                            {
                                _deferredQueue.Enqueue(coro);
                                continue;
                            }
                            
                            if (coro.MoveNext())
                            {
                                // More work to do
                                _activeJobs.Enqueue(coro);
                            }
                        }
                        else
                        {
                            // Process in-progress buffer
                            if (_activeJobs.Count == 0) break;

                            var next = _activeJobs.Dequeue();
                            if (next.MoveNext())
                            {
                                _activeJobs.Enqueue(next);
                            } else if (IsHeavy(next))
                            {
                                _activeHeavy--;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        UniLog.Warning("Exception integrating asset: " + ex);
                    }

                    elapsed = stopwatch.GetElapsedMilliseconds();
                } while (elapsed < maxMilliseconds);

                __result = workCount;

                if (!_triggered)
                {
                    _triggered = true;
                    UniLog.Log("MeshLoadSequencer processed " + workCount + " actions in " + stopwatch.ElapsedMilliseconds + "ms.");
                }
                
                return false;
            }
        }
    }
}
