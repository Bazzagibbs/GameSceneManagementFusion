//  Adaptation of Fusion.NetworkSceneManagerBase + Fusion.NetworkSceneManagerDefault
//  

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Fusion;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;

namespace BazzaGibbs.GameSceneManagement {
    public class GameSceneManagerFusion : Fusion.Behaviour, INetworkSceneManager {
        private static WeakReference<GameSceneManagerFusion> s_CurrentlyLoading = new(null);
        public NetworkRunner Runner { get; private set; }
        
        public int gameLevelSceneOffset = 1000;
        public static GameLevel[] registeredLevels => GameSceneManager.Instance.registeredLevels;
        public static GameLevel offlineLevel => GameSceneManager.offlineLevel;

        private SceneRef m_CurrentLevelRef;
        private bool m_CurrentLevelOutdated = false;
        private bool m_LoadingInProgress = false;

        protected virtual void LateUpdate() {
            if (!Runner) {
                return;
            }

            if (Runner.CurrentScene != m_CurrentLevelRef) {
                m_CurrentLevelOutdated = true;
            }

            if (!m_CurrentLevelOutdated || m_LoadingInProgress) {
                return;
            }
            
            if (s_CurrentlyLoading.TryGetTarget(out GameSceneManagerFusion target)) {
                Assert.Check(target != this);
                if (!target) {
                    s_CurrentlyLoading.SetTarget(null);
                }
                else {
                    // Wait for scene manager to finish loading
                    return;
                }
            }

            SceneRef prevLevel = m_CurrentLevelRef;
            m_CurrentLevelRef = Runner.CurrentScene;
            m_CurrentLevelOutdated = false;
            
            // Loading level {prevLevel} -> {m_CurrentLevelRef}

            _ = SetLevelAsync(m_CurrentLevelRef);
        }


        public List<NetworkObject> FindNetworkObjects(LoadedSceneCollection scenes, bool disable = true, bool addVisibilityNodes = false) {
            List<NetworkObject> networkObjects = new();
            List<NetworkObject> result = new();
            foreach (SceneInstance sceneInstance in scenes.sceneInstances) {
                GameObject[] gameObjects = sceneInstance.Scene.GetRootGameObjects();

                foreach (GameObject go in gameObjects) {
                    networkObjects.Clear();
                    go.GetComponentsInChildren(true, networkObjects);

                    foreach (NetworkObject sceneObject in networkObjects) {
                        if (sceneObject.Flags.IsSceneObject()) {
                            if (sceneObject.gameObject.activeInHierarchy || sceneObject.Flags.IsActivatedByUser()) {
                                Assert.Check(sceneObject.NetworkGuid.IsValid);
                                result.Add(sceneObject);
                            }
                        }
                    }

                    if (addVisibilityNodes) {
                        RunnerVisibilityNode.AddVisibilityNodes(go, Runner);
                    }

                    if (disable) {
                        foreach (NetworkObject sceneObject in result) {
                            sceneObject.gameObject.SetActive(false);
                        }
                    }
                }
            } 

            return result;
        }
       
#region INetworkSceneManager
        void INetworkSceneManager.Initialize(NetworkRunner runner) {
            Initialize(runner);
        }

        void INetworkSceneManager.Shutdown(NetworkRunner runner) {
            Shutdown(runner);
        }

        bool INetworkSceneManager.IsReady(NetworkRunner runner) {
            Assert.Check(Runner == runner);
           
            if (m_LoadingInProgress) return false;
            if (m_CurrentLevelOutdated) return false;
            if (runner.CurrentScene != m_CurrentLevelRef) return false;
            
            return true;
        }
#endregion

        protected virtual void Initialize(NetworkRunner runner) {
            Assert.Check(!Runner);
            Runner = runner;
        }

        protected virtual void Shutdown(NetworkRunner runner) {
            Assert.Check(Runner == runner);

            Runner = null;
            if (TryGetLevelRef(offlineLevel, out SceneRef levelRef)) {
                m_CurrentLevelRef = levelRef;
            } 
            m_CurrentLevelOutdated = false;
        }

        protected delegate void FinishedLoadingDelegate(IEnumerable<NetworkObject> sceneObjects);

        private async Task<LoadedSceneCollection> SetLevelGSMIntegration(SceneRef levelRef, FinishedLoadingDelegate finished) {
            if (TryGetLevelAsset(levelRef, out GameLevel gameLevel)) {
                LoadedSceneCollection loadedLevel = await GameSceneManager.SetLevelAsync(gameLevel);
                
            }
            else {
                Debug.LogError($"Could not load SceneRef {levelRef}");
            }
            
            return null;
        }

        public bool TryGetLevelAsset(SceneRef levelRef, out GameLevel levelAsset) {
            int index = levelRef;
            if (index < gameLevelSceneOffset || index >= gameLevelSceneOffset + registeredLevels.Length) {
                levelAsset = null;
                return false;
            }

            levelAsset = registeredLevels[index - gameLevelSceneOffset];
            return true;
        }

        public bool TryGetLevelRef(GameLevel levelAsset, out SceneRef levelRef) {
            for(int i = 0; i < registeredLevels.Length; i++) {
                if (registeredLevels[i] == levelAsset) {
                    levelRef = i + gameLevelSceneOffset;
                    return true;
                }
            }
            
            levelRef = SceneRef.None;
            return false;
        }

        private async Task SetLevelAsync(SceneRef level) {
            bool finishCalled = false;
            Dictionary<Guid, NetworkObject> levelObjects = new();


            FinishedLoadingDelegate callback = (objects) => {
                finishCalled = true;
                foreach (NetworkObject obj in objects) {
                    levelObjects.Add(obj.NetworkGuid, obj);
                }
            };

            try {
                Assert.Check(!s_CurrentlyLoading.TryGetTarget(out _));
                s_CurrentlyLoading.SetTarget(this);
                Runner.InvokeSceneLoadStart();

                await SetLevelGSMIntegration(level, callback);
            }
            finally {
                Assert.Check(s_CurrentlyLoading.TryGetTarget(out GameSceneManagerFusion target) && target == this);
                s_CurrentlyLoading.SetTarget(null);
                m_LoadingInProgress = false;
            }

            if (!finishCalled) {
                Debug.LogError($"Failed to switch scenes: SwitchLevel implementation did not invoke finished delegate");
            }
            else {
                Runner.RegisterSceneObjects(levelObjects.Values);
                Runner.InvokeSceneLoadDone();
            }
        }

    }
    
    

}
