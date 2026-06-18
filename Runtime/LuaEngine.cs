using UnityEngine;
using MoonSharp.Interpreter;
using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
#if XR_INTERACTION_TOOLKIT
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
#endif

namespace CreatorSDK
{
#if XR_INTERACTION_TOOLKIT
    /// <summary>
    /// Custom XRSocketInteractor that overrides hovered/selected evaluation targeting specific string tags.
    /// </summary>
    public class TaggedXRSocketInteractor : XRSocketInteractor
    {
        public string requiredTag = "";

        public override bool CanHover(IXRHoverInteractable interactable)
        {
            bool canHover = base.CanHover(interactable);
            if (!canHover) return false;

            if (string.IsNullOrEmpty(requiredTag)) return true;

            var bridge = interactable.transform.GetComponent<ScenarioBridge>();
            if (bridge != null && !string.IsNullOrEmpty(bridge.snapTag) && bridge.snapTag == requiredTag)
            {
                return true;
            }
            return false;
        }

        public override bool CanSelect(IXRSelectInteractable interactable)
        {
            bool canSelect = base.CanSelect(interactable);
            if (!canSelect) return false;

            if (string.IsNullOrEmpty(requiredTag)) return true;

            var bridge = interactable.transform.GetComponent<ScenarioBridge>();
            if (bridge != null && !string.IsNullOrEmpty(bridge.snapTag) && bridge.snapTag == requiredTag)
            {
                return true;
            }
            return false;
        }
    }
#endif

    /// <summary>
    /// Optional key/group metadata for ParticleSystem lookup by LuaEngine.
    /// Attach to the same GameObject as a ParticleSystem.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Creator SDK/Particle Tag")]
    public class ParticleTag : MonoBehaviour
    {
        [Tooltip("Optional custom key used by Lua API (PlayParticle, StopParticle, etc.)")]
        public string Name = "";

        [Tooltip("Optional group name for batch control (PlayGroup, StopGroup, etc.)")]
        public string Group = "";
    }

    /// <summary>
    /// Lua execution engine using MoonSharp
    /// Attach this to any GameObject with ScenarioBridge to run Lua scripts
    /// </summary>
    [RequireComponent(typeof(ScenarioBridge))]
    [AddComponentMenu("Creator SDK/Lua Engine")]
    public class LuaEngine : MonoBehaviour
    {
        // Static registry of all LuaEngine instances for FindObject
        private static Dictionary<string, LuaEngine> luaEngineRegistry = new Dictionary<string, LuaEngine>();

        private Script luaScript;
        private ScenarioBridge bridge;

        // Cached particle systems for fast lookup from Lua API
        private readonly Dictionary<string, ParticleSystem> particleSystemsByKey = new Dictionary<string, ParticleSystem>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ParticleSystem>> particleGroups = new Dictionary<string, List<ParticleSystem>>(StringComparer.Ordinal);
        private ParticleSystem defaultParticleSystem;

        [SerializeField] private bool logParticleRegistration = false;

        private const string DefaultParticleKey = "default";

        // Cached Lua functions
        private DynValue onStartFunc;
        private DynValue onUpdateFunc;
        private DynValue onInteractFunc;
        private DynValue onTriggerEnterFunc;
        private DynValue onTriggerExitFunc;

        // XR Cached Lua functions
        private DynValue onGrabFunc;
        private DynValue onReleaseFunc;
        private DynValue onHoverEnterFunc;
        private DynValue onHoverExitFunc;
        private DynValue onActivateFunc;
        private DynValue onDeactivateFunc;
        private DynValue onSnapFunc;
        private DynValue onUnsnapFunc;

        // Coroutine management
        private List<LuaCoroutineInfo> activeCoroutines = new List<LuaCoroutineInfo>();

        // Store the self table for external access
        private Table selfTable;

        private bool isInitialized = false;

#if XR_INTERACTION_TOOLKIT
        // XR Interaction
        private XRBaseInteractable xrInteractable;
        private XRBaseInteractor xrInteractor;
#endif

        // Helper class to track Lua coroutines
        private class LuaCoroutineInfo
        {
            public DynValue coroutine;
            public float waitUntilTime;
            public bool isWaiting;
        }

        private T GetComponentOrWarn<T>(string apiName) where T : Component
        {
            T component = GetComponent<T>();
            if (component == null)
            {
                Debug.LogWarning($"[LuaEngine] {apiName}: No {typeof(T).Name} on {gameObject.name}");
            }
            return component;
        }

        private void InitializeParticleRegistry()
        {
            particleSystemsByKey.Clear();
            particleGroups.Clear();
            defaultParticleSystem = null;

            ParticleSystem[] particleSystems = GetComponentsInChildren<ParticleSystem>(true);

            for (int i = 0; i < particleSystems.Length; i++)
            {
                ParticleSystem particleSystem = particleSystems[i];
                if (particleSystem == null)
                    continue;

                if (defaultParticleSystem == null)
                    defaultParticleSystem = particleSystem;

                string key = ResolveParticleKey(particleSystem, i);
                string registeredKey = RegisterParticleSystem(key, particleSystem);

                ParticleTag tag = particleSystem.GetComponent<ParticleTag>();
                if (tag != null && !string.IsNullOrWhiteSpace(tag.Group))
                {
                    RegisterParticleGroup(tag.Group.Trim(), particleSystem);
                }

                if (logParticleRegistration)
                {
                    Debug.Log($"[LuaEngine] Registered ParticleSystem '{registeredKey}' on {gameObject.name}");
                }
            }

            if (defaultParticleSystem != null)
            {
                particleSystemsByKey[DefaultParticleKey] = defaultParticleSystem;
            }
            else if (logParticleRegistration)
            {
                Debug.Log($"[LuaEngine] No ParticleSystem found on {gameObject.name} (including children)");
            }
        }

        private string ResolveParticleKey(ParticleSystem particleSystem, int fallbackIndex)
        {
            ParticleTag tag = particleSystem.GetComponent<ParticleTag>();
            if (tag != null && !string.IsNullOrWhiteSpace(tag.Name))
            {
                return tag.Name.Trim();
            }

            string objectName = particleSystem.gameObject.name;
            if (!string.IsNullOrWhiteSpace(objectName))
            {
                return objectName.Trim();
            }

            return $"Particle_{fallbackIndex}";
        }

        private string RegisterParticleSystem(string key, ParticleSystem particleSystem)
        {
            string baseKey = string.IsNullOrWhiteSpace(key) ? "Particle" : key;

            if (!particleSystemsByKey.TryGetValue(baseKey, out ParticleSystem existing))
            {
                particleSystemsByKey[baseKey] = particleSystem;
                return baseKey;
            }

            if (existing == particleSystem)
            {
                return baseKey;
            }

            int suffix = 2;
            string uniqueKey = $"{baseKey}_{suffix}";
            while (particleSystemsByKey.ContainsKey(uniqueKey))
            {
                suffix++;
                uniqueKey = $"{baseKey}_{suffix}";
            }

            particleSystemsByKey[uniqueKey] = particleSystem;
            Debug.LogWarning($"[LuaEngine] Duplicate particle key '{baseKey}' on {gameObject.name}. Registered as '{uniqueKey}'.");
            return uniqueKey;
        }

        private void RegisterParticleGroup(string groupName, ParticleSystem particleSystem)
        {
            if (!particleGroups.TryGetValue(groupName, out List<ParticleSystem> group))
            {
                group = new List<ParticleSystem>();
                particleGroups[groupName] = group;
            }

            if (!group.Contains(particleSystem))
            {
                group.Add(particleSystem);
            }
        }

        private bool TryGetParticleSystem(string key, string apiName, out ParticleSystem particleSystem)
        {
            if (particleSystemsByKey.Count == 0)
            {
                InitializeParticleRegistry();
            }

            string resolvedKey = string.IsNullOrWhiteSpace(key) ? DefaultParticleKey : key.Trim();
            if (particleSystemsByKey.TryGetValue(resolvedKey, out particleSystem) && particleSystem != null)
            {
                return true;
            }

            particleSystem = null;
            Debug.LogWarning($"[LuaEngine] {apiName}: Particle key '{resolvedKey}' not found on {gameObject.name}");
            return false;
        }

        private bool TryGetParticleGroup(string groupName, string apiName, out List<ParticleSystem> group)
        {
            if (particleSystemsByKey.Count == 0)
            {
                InitializeParticleRegistry();
            }

            if (!string.IsNullOrWhiteSpace(groupName) &&
                particleGroups.TryGetValue(groupName.Trim(), out group) &&
                group != null &&
                group.Count > 0)
            {
                return true;
            }

            group = null;
            Debug.LogWarning($"[LuaEngine] {apiName}: Particle group '{groupName}' not found on {gameObject.name}");
            return false;
        }

        private void ExecuteParticle(string apiName, string key, Action<ParticleSystem> action)
        {
            if (TryGetParticleSystem(key, apiName, out ParticleSystem particleSystem))
            {
                action(particleSystem);
            }
        }

        private bool QueryParticle(string apiName, string key, Func<ParticleSystem, bool> query)
        {
            if (TryGetParticleSystem(key, apiName, out ParticleSystem particleSystem))
            {
                return query(particleSystem);
            }

            return false;
        }

        private void ExecuteParticleGroup(string apiName, string groupName, Action<ParticleSystem> action)
        {
            if (!TryGetParticleGroup(groupName, apiName, out List<ParticleSystem> group))
                return;

            for (int i = 0; i < group.Count; i++)
            {
                ParticleSystem particleSystem = group[i];
                if (particleSystem != null)
                {
                    action(particleSystem);
                }
            }
        }

        private void PlayParticle(string key)
        {
            ExecuteParticle("PlayParticle", key, particleSystem => particleSystem.Play(true));
        }

        private void PauseParticle(string key)
        {
            ExecuteParticle("PauseParticle", key, particleSystem => particleSystem.Pause(true));
        }

        private void StopParticle(string key, bool clear)
        {
            ExecuteParticle("StopParticle", key, particleSystem =>
            {
                particleSystem.Stop(true, clear
                    ? ParticleSystemStopBehavior.StopEmittingAndClear
                    : ParticleSystemStopBehavior.StopEmitting);
            });
        }

        private void ClearParticle(string key)
        {
            ExecuteParticle("ClearParticle", key, particleSystem => particleSystem.Clear(true));
        }

        private void SetParticleLoop(string key, bool loop)
        {
            ExecuteParticle("SetParticleLoop", key, particleSystem =>
            {
                ParticleSystem.MainModule main = particleSystem.main;
                main.loop = loop;
            });
        }

        private bool IsParticlePlaying(string key)
        {
            return QueryParticle("IsPlaying", key, particleSystem => particleSystem.isPlaying);
        }

        private void PlayParticleGroup(string groupName)
        {
            ExecuteParticleGroup("PlayGroup", groupName, particleSystem => particleSystem.Play(true));
        }

        private void PauseParticleGroup(string groupName)
        {
            ExecuteParticleGroup("PauseGroup", groupName, particleSystem => particleSystem.Pause(true));
        }

        private void StopParticleGroup(string groupName, bool clear)
        {
            ExecuteParticleGroup("StopGroup", groupName, particleSystem =>
            {
                particleSystem.Stop(true, clear
                    ? ParticleSystemStopBehavior.StopEmittingAndClear
                    : ParticleSystemStopBehavior.StopEmitting);
            });
        }

        private void ClearParticleGroup(string groupName)
        {
            ExecuteParticleGroup("ClearGroup", groupName, particleSystem => particleSystem.Clear(true));
        }

        private bool IsParticleGroupPlaying(string groupName)
        {
            if (!TryGetParticleGroup(groupName, "IsGroupPlaying", out List<ParticleSystem> group))
                return false;

            for (int i = 0; i < group.Count; i++)
            {
                ParticleSystem particleSystem = group[i];
                if (particleSystem != null && particleSystem.isPlaying)
                    return true;
            }

            return false;
        }

        public Table GetOrInitializeSelfTable()
        {
            if (selfTable == null)
            {
                if (bridge == null) bridge = GetComponent<ScenarioBridge>();
                if (bridge != null && bridge.luaScript != null)
                {
                    if (!isInitialized)
                        InitializeLua();
                }
                else
                {
                    luaScript = new Script();
                    RegisterAPI();
                    isInitialized = true;
                }
            }
            return selfTable;
        }

        /// <summary>
        /// Creates a proxy Table owned by <paramref name="callerScript"/> so MoonSharp
        /// never sees a cross-script Table ownership violation.
        /// All delegates still point at THIS engine's GameObject/transform.
        /// </summary>
        public Table CreateExternalTable(Script callerScript)
        {
            // Ensure this engine is initialised (needed for GetComponentOrWarn etc.)
            if (bridge == null) bridge = GetComponent<ScenarioBridge>();

            Table t = new Table(callerScript);

            // --- Transform / Visual ---
            t["GetName"]           = (Func<string>)(() => gameObject.name);
            t["SetPosition"]       = (Action<float,float,float>)((x,y,z) => transform.position = new Vector3(x,y,z));
            t["SetLocalPosition"]  = (Action<float,float,float>)((x,y,z) => transform.localPosition = new Vector3(x,y,z));
            t["SetRotation"]       = (Action<float,float,float>)((x,y,z) => transform.eulerAngles = new Vector3(x,y,z));
            t["SetScale"]          = (Action<float,float,float>)((x,y,z) => transform.localScale = new Vector3(x,y,z));
            t["Move"]              = (Action<float,float,float>)((x,y,z) => transform.Translate(x,y,z,Space.Self));
            t["Rotate"]            = (Action<float,float,float>)((x,y,z) => transform.Rotate(x,y,z,Space.Self));
            t["SetActive"]         = (Action<bool>)(active => gameObject.SetActive(active));
            t["SetColor"]          = (Action<float,float,float,float>)((r,g,b,a) =>
            {
                Renderer ren = GetComponent<Renderer>();
                if (ren != null) ren.material.color = new Color(r,g,b,a);
            });
            t["GetPosition"] = (Func<Table>)(() =>
            {
                Table pos = new Table(callerScript);
                pos["x"] = transform.position.x;
                pos["y"] = transform.position.y;
                pos["z"] = transform.position.z;
                return pos;
            });
            t["GetLocalPosition"] = (Func<Table>)(() =>
            {
                Table pos = new Table(callerScript);
                pos["x"] = transform.localPosition.x;
                pos["y"] = transform.localPosition.y;
                pos["z"] = transform.localPosition.z;
                return pos;
            });

            // --- Light ---
            t["SetLightEnabled"]   = (Action<bool>)(en =>
            {
                Light l = GetComponentOrWarn<Light>("SetLightEnabled");
                if (l != null) l.enabled = en;
            });
            t["SetLightColor"]     = (Action<float,float,float>)((r,g,b) =>
            {
                Light l = GetComponentOrWarn<Light>("SetLightColor");
                if (l != null) l.color = new Color(r,g,b);
            });
            t["SetLightIntensity"] = (Action<float>)(intensity =>
            {
                Light l = GetComponentOrWarn<Light>("SetLightIntensity");
                if (l != null) l.intensity = Mathf.Max(0f, intensity);
            });
            t["SetLightRange"]     = (Action<float>)(range =>
            {
                Light l = GetComponentOrWarn<Light>("SetLightRange");
                if (l != null) l.range = Mathf.Max(0f, range);
            });

            // --- Audio ---
            t["PlayAudio"]  = (Action)(() => { AudioSource a = GetComponentOrWarn<AudioSource>("PlayAudio");  if (a != null) a.Play(); });
            t["PauseAudio"] = (Action)(() => { AudioSource a = GetComponentOrWarn<AudioSource>("PauseAudio"); if (a != null) a.Pause(); });
            t["StopAudio"]  = (Action)(() => { AudioSource a = GetComponentOrWarn<AudioSource>("StopAudio");  if (a != null) a.Stop(); });

            // --- Utility ---
            t["Log"]     = (Action<string>)(msg => Debug.Log($"[Lua:{gameObject.name}] {msg}"));
            t["Destroy"] = (Action)(() => Destroy(gameObject));
            t["SetKinematic"] = (Action<bool>)(k =>
            {
                Rigidbody rb = GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = k;
            });

            // --- Hierarchy (recursive, stays in callerScript context) ---
            t["GetParent"] = (Func<DynValue>)(() =>
            {
                if (transform.parent != null)
                {
                    LuaEngine pe = transform.parent.GetComponent<LuaEngine>();
                    if (pe == null) pe = transform.parent.gameObject.AddComponent<LuaEngine>();
                    return DynValue.NewTable(pe.CreateExternalTable(callerScript));
                }
                return DynValue.Nil;
            });
            t["GetChild"] = (Func<string, DynValue>)(childName =>
            {
                Transform child = transform.Find(childName);
                if (child != null)
                {
                    LuaEngine ce = child.GetComponent<LuaEngine>();
                    if (ce == null) ce = child.gameObject.AddComponent<LuaEngine>();
                    return DynValue.NewTable(ce.CreateExternalTable(callerScript));
                }
                return DynValue.Nil;
            });
            t["GetChildren"] = (Func<Table>)(() =>
            {
                Table arr = new Table(callerScript);
                int idx = 1;
                foreach (Transform child in transform)
                {
                    LuaEngine ce = child.GetComponent<LuaEngine>();
                    if (ce == null) ce = child.gameObject.AddComponent<LuaEngine>();
                    arr[idx++] = DynValue.NewTable(ce.CreateExternalTable(callerScript));
                }
                return arr;
            });

            return t;
        }

        private void Awake()
        {
            bridge = GetComponent<ScenarioBridge>();

            // Register this engine in the static registry
            if (!luaEngineRegistry.ContainsKey(gameObject.name))
            {
                luaEngineRegistry[gameObject.name] = this;
            }

            if (bridge == null)
            {
                Debug.LogError($"[LuaEngine] No ScenarioBridge found on {gameObject.name}");
                enabled = false;
                return;
            }

            
            InitializeParticleRegistry();

            if (bridge.luaScript != null)
            {
                if (!isInitialized)
                {
                    InitializeLua();
                }
                SetupXR();
            }
            else
            {
                GetOrInitializeSelfTable();
                enabled = false;
            }
        }

        /// <summary>
        /// Auto-setup XR interactable if XR Interaction Toolkit is installed.
        /// Adds XRSimpleInteractable and Collider if missing.
        /// </summary>
        private void SetupXR()
        {
#if XR_INTERACTION_TOOLKIT
            if (bridge.isSnapZone)
            {
                xrInteractor = GetComponent<XRBaseInteractor>();
                if (xrInteractor == null)
                {
                    xrInteractor = gameObject.AddComponent<TaggedXRSocketInteractor>();
                    Debug.Log($"[LuaEngine] Auto-added TaggedXRSocketInteractor to {gameObject.name}");
                }

                if (xrInteractor is TaggedXRSocketInteractor taggedSocket)
                {
                    taggedSocket.requiredTag = bridge.snapTag;
                    taggedSocket.showInteractableHoverMeshes = true; // Enables Ghost preview!

                    if (bridge.customAttachPoint != null)
                    {
                        taggedSocket.attachTransform = bridge.customAttachPoint;
                        Debug.Log($"[LuaEngine] Mapped Inspector Attach Point for Tagged Socket on {gameObject.name}");
                    }
                }
                else if (xrInteractor is XRSocketInteractor socket)
                {
                    socket.showInteractableHoverMeshes = true; // Enables Ghost preview!

                    if (bridge.customAttachPoint != null)
                    {
                        socket.attachTransform = bridge.customAttachPoint;
                        Debug.Log($"[LuaEngine] Mapped Inspector Attach Point for Socket on {gameObject.name}");
                    }
                }

                // Sockets require a Trigger collider to detect when objects hover them
                bool hasTrigger = false;
                foreach (var col in GetComponents<Collider>())
                {
                    if (col.isTrigger)
                    {
                        hasTrigger = true;
                        break;
                    }
                }

                if (!hasTrigger)
                {
                    var snapCol = gameObject.AddComponent<SphereCollider>();
                    snapCol.isTrigger = true;
                    snapCol.radius = 0.6f; // Make it slightly larger to catch objects easily
                    Debug.Log($"[LuaEngine] Auto-added SphereCollider (Trigger) to {gameObject.name} for snapping");
                }

                // For Socket, select is when something snaps into it
                xrInteractor.selectEntered.AddListener(OnXRSocketSelectEntered);
                xrInteractor.selectExited.AddListener(OnXRSocketSelectExited);
            }
            else
            {
                xrInteractable = GetComponent<XRBaseInteractable>();

                if (xrInteractable == null)
                {
                    if (bridge.isGrabbable)
                    {
                        var grabInteractable = gameObject.AddComponent<XRGrabInteractable>();
                        grabInteractable.movementType = XRBaseInteractable.MovementType.Kinematic;

                        if (bridge.customAttachPoint != null)
                        {
                            grabInteractable.attachTransform = bridge.customAttachPoint;
                        }

                        xrInteractable = grabInteractable;
                        Debug.Log($"[LuaEngine] Auto-added XRGrabInteractable to {gameObject.name}");
                    }
                    else
                    {
                        xrInteractable = gameObject.AddComponent<XRSimpleInteractable>();
                        Debug.Log($"[LuaEngine] Auto-added XRSimpleInteractable to {gameObject.name}");
                    }
                }

                if (GetComponent<Collider>() == null)
                {
                    gameObject.AddComponent<BoxCollider>();
                    Debug.Log($"[LuaEngine] Auto-added BoxCollider to {gameObject.name}");
                }

                if (bridge.isGrabbable && GetComponent<Rigidbody>() == null)
                {
                    var rb = gameObject.AddComponent<Rigidbody>();
                    rb.isKinematic = true;
                    Debug.Log($"[LuaEngine] Auto-added Rigidbody (Kinematic) to {gameObject.name}");
                }

                // Subscribe to XR events
                xrInteractable.selectEntered.AddListener(OnXRSelectEntered);
                xrInteractable.selectExited.AddListener(OnXRSelectExited);
                xrInteractable.hoverEntered.AddListener(OnXRHoverEntered);
                xrInteractable.hoverExited.AddListener(OnXRHoverExited);
                xrInteractable.activated.AddListener(OnXRActivated);
                xrInteractable.deactivated.AddListener(OnXRDeactivated);
            }

            Debug.Log($"[LuaEngine] ✓ XR enabled on {gameObject.name}");
#endif
        }

        private void OnDestroy()
        {
#if XR_INTERACTION_TOOLKIT
            // Unsubscribe from XR events
            if (xrInteractable != null)
            {
                xrInteractable.selectEntered.RemoveListener(OnXRSelectEntered);
                xrInteractable.selectExited.RemoveListener(OnXRSelectExited);
                xrInteractable.hoverEntered.RemoveListener(OnXRHoverEntered);
                xrInteractable.hoverExited.RemoveListener(OnXRHoverExited);
                xrInteractable.activated.RemoveListener(OnXRActivated);
                xrInteractable.deactivated.RemoveListener(OnXRDeactivated);
            }

            if (xrInteractor != null)
            {
                xrInteractor.selectEntered.RemoveListener(OnXRSocketSelectEntered);
                xrInteractor.selectExited.RemoveListener(OnXRSocketSelectExited);
            }
#endif
            // Unregister from the static registry
            if (luaEngineRegistry.ContainsKey(gameObject.name) && luaEngineRegistry[gameObject.name] == this)
            {
                luaEngineRegistry.Remove(gameObject.name);
            }
        }

        private void InitializeLua()
        {
            try
            {
                // Create new Lua script environment
                luaScript = new Script();

                // Register all Unity API bindings
                RegisterAPI();

                luaScript.DoString(@"
                    function wait(seconds)
                        coroutine.yield(seconds)
                    end
                ");

                // Execute the Lua script
                luaScript.DoString(bridge.luaScript.text);

                // Cache function references for performance
                onStartFunc = luaScript.Globals.Get("OnStart");
                onUpdateFunc = luaScript.Globals.Get("OnUpdate");
                onInteractFunc = luaScript.Globals.Get("OnInteract");
                onTriggerEnterFunc = luaScript.Globals.Get("OnTriggerEnter");
                onTriggerExitFunc = luaScript.Globals.Get("OnTriggerExit");

                // XR callbacks
                onGrabFunc = luaScript.Globals.Get("OnGrab");
                onReleaseFunc = luaScript.Globals.Get("OnRelease");
                onHoverEnterFunc = luaScript.Globals.Get("OnHoverEnter");
                onHoverExitFunc = luaScript.Globals.Get("OnHoverExit");
                onActivateFunc = luaScript.Globals.Get("OnActivate");
                onDeactivateFunc = luaScript.Globals.Get("OnDeactivate");
                onSnapFunc = luaScript.Globals.Get("OnSnap");
                onUnsnapFunc = luaScript.Globals.Get("OnUnsnap");

                isInitialized = true;
                Debug.Log($"[LuaEngine] ✓ Script loaded on {gameObject.name}");
            }
            catch (SyntaxErrorException ex)
            {
                Debug.LogError($"[LuaEngine] Syntax error in {gameObject.name}: {ex.DecoratedMessage}");
            }
            catch (ScriptRuntimeException ex)
            {
                Debug.LogError($"[LuaEngine] Runtime error in {gameObject.name}: {ex.DecoratedMessage}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LuaEngine] Error loading script on {gameObject.name}: {ex.Message}");
            }
        }

        private void RegisterAPI()
        {
            // ===== SELF TABLE (represents this GameObject) =====
            selfTable = new Table(luaScript);
            luaScript.Globals["self"] = selfTable;

            // --- Transform Functions ---
            selfTable["Rotate"] = (Action<float, float, float>)((x, y, z) =>
            {
                transform.Rotate(x, y, z, Space.Self);
            });

            selfTable["SetPosition"] = (Action<float, float, float>)((x, y, z) =>
            {
                transform.position = new Vector3(x, y, z);
            });

            selfTable["GetPosition"] = (Func<Table>)(() =>
            {
                Table pos = new Table(luaScript);
                pos["x"] = transform.position.x;
                pos["y"] = transform.position.y;
                pos["z"] = transform.position.z;
                return pos;
            });
            //================= local position =================\\

            selfTable["SetLocalPosition"] = (Action<float, float, float>)((x, y, z) =>
            {
                transform.localPosition = new Vector3(x, y, z);
            });

            selfTable["GetLocalPosition"] = (Func<Table>)(() =>
            {
                Table pos = new Table(luaScript);
                pos["x"] = transform.localPosition.x;
                pos["y"] = transform.localPosition.y;
                pos["z"] = transform.localPosition.z;
                return pos;
            });
            // ======================== \\
            selfTable["SetRotation"] = (Action<float, float, float>)((x, y, z) =>
            {
                transform.eulerAngles = new Vector3(x, y, z);
            });

            selfTable["GetRotation"] = (Func<Table>)(() =>
            {
                Table rot = new Table(luaScript);
                rot["x"] = transform.eulerAngles.x;
                rot["y"] = transform.eulerAngles.y;
                rot["z"] = transform.eulerAngles.z;
                return rot;
            });

            selfTable["SetScale"] = (Action<float, float, float>)((x, y, z) =>
            {
                transform.localScale = new Vector3(x, y, z);
            });

            selfTable["GetScale"] = (Func<Table>)(() =>
            {
                Table scale = new Table(luaScript);
                scale["x"] = transform.localScale.x;
                scale["y"] = transform.localScale.y;
                scale["z"] = transform.localScale.z;
                return scale;
            });

            selfTable["Move"] = (Action<float, float, float>)((x, y, z) =>
            {
                transform.Translate(x, y, z, Space.Self);
            });

            selfTable["LookAt"] = (Action<float, float, float>)((x, y, z) =>
            {
                transform.LookAt(new Vector3(x, y, z));
            });

            selfTable["RotateAround"] = (Action<float, float, float, float, float, float, float>)((px, py, pz, ax, ay, az, angle) =>
            {
                transform.RotateAround(new Vector3(px, py, pz), new Vector3(ax, ay, az), angle);
            });

            // --- Visual Functions ---
            selfTable["SetColor"] = (Action<float, float, float, float>)((r, g, b, a) =>
            {
                Renderer renderer = GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.material.color = new Color(r, g, b, a);
                }
                else
                {
                    Debug.LogWarning($"[LuaEngine] SetColor: No Renderer on {gameObject.name}");
                }
            });

            selfTable["SetActive"] = (Action<bool>)((active) =>
            {
                gameObject.SetActive(active);
            });

            // --- Light Functions ---
            selfTable["SetLightEnabled"] = (Action<bool>)((enabled) =>
            {
                Light light = GetComponentOrWarn<Light>("SetLightEnabled");
                if (light != null)
                    light.enabled = enabled;
            });

            selfTable["SetLightColor"] = (Action<float, float, float>)((r, g, b) =>
            {
                Light light = GetComponentOrWarn<Light>("SetLightColor");
                if (light != null)
                    light.color = new Color(r, g, b);
            });

            selfTable["SetLightIntensity"] = (Action<float>)((intensity) =>
            {
                Light light = GetComponentOrWarn<Light>("SetLightIntensity");
                if (light != null)
                    light.intensity = Mathf.Max(0f, intensity);
            });

            selfTable["SetLightRange"] = (Action<float>)((range) =>
            {
                Light light = GetComponentOrWarn<Light>("SetLightRange");
                if (light != null)
                    light.range = Mathf.Max(0f, range);
            });

            // --- Audio Functions ---
            selfTable["PlayAudio"] = (Action)(() =>
            {
                AudioSource audioSource = GetComponentOrWarn<AudioSource>("PlayAudio");
                if (audioSource != null)
                    audioSource.Play();
            });

            selfTable["PauseAudio"] = (Action)(() =>
            {
                AudioSource audioSource = GetComponentOrWarn<AudioSource>("PauseAudio");
                if (audioSource != null)
                    audioSource.Pause();
            });

            selfTable["StopAudio"] = (Action)(() =>
            {
                AudioSource audioSource = GetComponentOrWarn<AudioSource>("StopAudio");
                if (audioSource != null)
                    audioSource.Stop();
            });

            selfTable["SetAudioVolume"] = (Action<float>)((volume) =>
            {
                AudioSource audioSource = GetComponentOrWarn<AudioSource>("SetAudioVolume");
                if (audioSource != null)
                    audioSource.volume = Mathf.Clamp01(volume);
            });

            selfTable["SetAudioPitch"] = (Action<float>)((pitch) =>
            {
                AudioSource audioSource = GetComponentOrWarn<AudioSource>("SetAudioPitch");
                if (audioSource != null)
                    audioSource.pitch = Mathf.Clamp(pitch, -3f, 3f);
            });

            selfTable["SetAudioLoop"] = (Action<bool>)((loop) =>
            {
                AudioSource audioSource = GetComponentOrWarn<AudioSource>("SetAudioLoop");
                if (audioSource != null)
                    audioSource.loop = loop;
            });

            selfTable["SetAudioMute"] = (Action<bool>)((mute) =>
            {
                AudioSource audioSource = GetComponentOrWarn<AudioSource>("SetAudioMute");
                if (audioSource != null)
                    audioSource.mute = mute;
            });

            selfTable["IsAudioPlaying"] = (Func<bool>)(() =>
            {
                AudioSource audioSource = GetComponentOrWarn<AudioSource>("IsAudioPlaying");
                return audioSource != null && audioSource.isPlaying;
            });

            // --- Particle Functions ---
            selfTable["RefreshParticles"] = (Action)(InitializeParticleRegistry);

            selfTable["PlayParticle"] = (Action<string>)((name) =>
            {
                PlayParticle(name);
            });

            selfTable["PauseParticle"] = (Action<string>)((name) =>
            {
                PauseParticle(name);
            });

            selfTable["StopParticle"] = (Action<string, bool>)((name, clear) =>
            {
                StopParticle(name, clear);
            });

            selfTable["ClearParticle"] = (Action<string>)((name) =>
            {
                ClearParticle(name);
            });

            selfTable["SetParticleLoop"] = (Action<string, bool>)((name, loop) =>
            {
                SetParticleLoop(name, loop);
            });

            selfTable["IsPlaying"] = (Func<string, bool>)((name) =>
            {
                return IsParticlePlaying(name);
            });

            selfTable["PlayGroup"] = (Action<string>)((groupName) =>
            {
                PlayParticleGroup(groupName);
            });

            selfTable["PauseGroup"] = (Action<string>)((groupName) =>
            {
                PauseParticleGroup(groupName);
            });

            selfTable["StopGroup"] = (Action<string, bool>)((groupName, clear) =>
            {
                StopParticleGroup(groupName, clear);
            });

            selfTable["ClearGroup"] = (Action<string>)((groupName) =>
            {
                ClearParticleGroup(groupName);
            });

            selfTable["IsGroupPlaying"] = (Func<string, bool>)((groupName) =>
            {
                return IsParticleGroupPlaying(groupName);
            });

            // Backward-compatible single-particle aliases (operate on key: "default")
            selfTable["PlayParticles"] = (Action)(() =>
            {
                PlayParticle(DefaultParticleKey);
            });

            selfTable["PauseParticles"] = (Action)(() =>
            {
                PauseParticle(DefaultParticleKey);
            });

            selfTable["StopParticles"] = (Action<bool>)((clear) =>
            {
                StopParticle(DefaultParticleKey, clear);
            });

            selfTable["ClearParticles"] = (Action)(() =>
            {
                ClearParticle(DefaultParticleKey);
            });

            selfTable["SetParticlesLoop"] = (Action<bool>)((loop) =>
            {
                SetParticleLoop(DefaultParticleKey, loop);
            });

            selfTable["IsParticlesPlaying"] = (Func<bool>)(() =>
            {
                return IsParticlePlaying(DefaultParticleKey);
            });

            // --- Animation Functions ---
            selfTable["PlayAnimation"] = (Action<string>)((animName) =>
            {
                Animator animator = GetComponent<Animator>();
                if (animator != null)
                {
                    animator.Play(animName);
                }
                else
                {
                    Debug.LogWarning($"[LuaEngine] PlayAnimation: No Animator on {gameObject.name}");
                }
            });

            selfTable["SetAnimationBool"] = (Action<string, bool>)((paramName, state) =>
            {
                Animator animator = GetComponent<Animator>();
                if (animator != null)
                {
                    animator.SetBool(paramName, state);
                }
                else
                {
                    Debug.LogWarning($"[LuaEngine] SetAnimationBool: No Animator on {gameObject.name}");
                }
            });

            selfTable["SetAnimationTrigger"] = (Action<string>)((paramName) =>
            {
                Animator animator = GetComponent<Animator>();
                if (animator != null)
                {
                    animator.SetTrigger(paramName);
                }
                else
                {
                    Debug.LogWarning($"[LuaEngine] SetAnimationTrigger: No Animator on {gameObject.name}");
                }
            });

            // --- Utility Functions ---
            selfTable["Log"] = (Action<string>)((msg) =>
            {
                Debug.Log($"[Lua:{gameObject.name}] {msg}");
            });

            selfTable["Destroy"] = (Action)(() =>
            {
                Destroy(gameObject);
            });

            selfTable["GetName"] = (Func<string>)(() => gameObject.name);

            // --- Input Functions ---
            selfTable["GetKey"] = (Func<string, bool>)((keyName) =>
            {
                try
                {
                    KeyCode key = (KeyCode)Enum.Parse(typeof(KeyCode), keyName, true);
                    return Input.GetKey(key);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LuaEngine] GetKey('{keyName}') failed: {ex.Message}. Make sure Active Input Handling is set to 'Both' in Player Settings.");
                    return false;
                }
            });

            selfTable["GetKeyDown"] = (Func<string, bool>)((keyName) =>
            {
                try
                {
                    KeyCode key = (KeyCode)Enum.Parse(typeof(KeyCode), keyName, true);
                    return Input.GetKeyDown(key);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LuaEngine] GetKeyDown('{keyName}') failed: {ex.Message}. Make sure Active Input Handling is set to 'Both' in Player Settings.");
                    return false;
                }
            });

            selfTable["GetKeyUp"] = (Func<string, bool>)((keyName) =>
            {
                try
                {
                    KeyCode key = (KeyCode)Enum.Parse(typeof(KeyCode), keyName, true);
                    return Input.GetKeyUp(key);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LuaEngine] GetKeyUp('{keyName}') failed: {ex.Message}. Make sure Active Input Handling is set to 'Both' in Player Settings.");
                    return false;
                }
            });

            selfTable["GetMouseButton"] = (Func<int, bool>)((button) =>
            {
                return Input.GetMouseButton(button);
            });

            selfTable["GetMouseButtonDown"] = (Func<int, bool>)((button) =>
            {
                return Input.GetMouseButtonDown(button);
            });

            // --- Find Other Objects ---
            selfTable["FindObject"] = (Func<string, DynValue>)((objectName) =>
            {
                if (luaEngineRegistry.TryGetValue(objectName, out LuaEngine otherEngine))
                {
                    if (otherEngine != null && otherEngine.isInitialized && otherEngine.selfTable != null)
                    {
                        return DynValue.NewTable(otherEngine.selfTable);
                    }
                }

                // Fallback: try to find by searching all LuaEngines in scene
                LuaEngine[] allEngines = FindObjectsByType<LuaEngine>(FindObjectsSortMode.None);
                foreach (var engine in allEngines)
                {
                    if (engine.gameObject.name == objectName && engine.isInitialized && engine.selfTable != null)
                    {
                        return DynValue.NewTable(engine.selfTable);
                    }
                }

                Debug.LogWarning($"[LuaEngine] Object not found: {objectName}");
                return DynValue.Nil;
            });

            // --- Rigidbody / Physics Functions ---
            selfTable["AddForce"] = (Action<float, float, float>)((x, y, z) =>
            {
                Rigidbody rb = GetComponent<Rigidbody>();
                if (rb != null)
                    rb.AddForce(new Vector3(x, y, z), ForceMode.Force);
                else
                    Debug.LogWarning($"[LuaEngine] AddForce: No Rigidbody on {gameObject.name}");
            });

            selfTable["AddImpulse"] = (Action<float, float, float>)((x, y, z) =>
            {
                Rigidbody rb = GetComponent<Rigidbody>();
                if (rb != null)
                    rb.AddForce(new Vector3(x, y, z), ForceMode.Impulse);
                else
                    Debug.LogWarning($"[LuaEngine] AddImpulse: No Rigidbody on {gameObject.name}");
            });

            selfTable["SetVelocity"] = (Action<float, float, float>)((x, y, z) =>
            {
                Rigidbody rb = GetComponent<Rigidbody>();
                if (rb != null)
                    rb.linearVelocity = new Vector3(x, y, z);
                else
                    Debug.LogWarning($"[LuaEngine] SetVelocity: No Rigidbody on {gameObject.name}");
            });

            selfTable["GetVelocity"] = (Func<Table>)(() =>
            {
                Rigidbody rb = GetComponent<Rigidbody>();
                Table vel = new Table(luaScript);
                if (rb != null)
                {
                    vel["x"] = rb.linearVelocity.x;
                    vel["y"] = rb.linearVelocity.y;
                    vel["z"] = rb.linearVelocity.z;
                }
                else
                {
                    vel["x"] = 0f; vel["y"] = 0f; vel["z"] = 0f;
                }
                return vel;
            });

            selfTable["SetKinematic"] = (Action<bool>)((isKinematic) =>
            {
                Rigidbody rb = GetComponent<Rigidbody>();
                if (rb != null)
                    rb.isKinematic = isKinematic;
                else
                    Debug.LogWarning($"[LuaEngine] SetKinematic: No Rigidbody on {gameObject.name}");
            });

            selfTable["AddTorque"] = (Action<float, float, float>)((x, y, z) =>
            {
                Rigidbody rb = GetComponent<Rigidbody>();
                if (rb != null)
                    rb.AddTorque(new Vector3(x, y, z), ForceMode.Force);
                else
                    Debug.LogWarning($"[LuaEngine] AddTorque: No Rigidbody on {gameObject.name}");
            });

            selfTable["GetMass"] = (Func<float>)(() =>
            {
                Rigidbody rb = GetComponent<Rigidbody>();
                return rb != null ? rb.mass : 0f;
            });

            selfTable["SetMass"] = (Action<float>)((mass) =>
            {
                Rigidbody rb = GetComponent<Rigidbody>();
                if (rb != null)
                    rb.mass = mass;
                else
                    Debug.LogWarning($"[LuaEngine] SetMass: No Rigidbody on {gameObject.name}");
            });

            selfTable["SetDrag"] = (Action<float>)((drag) =>
            {
                Rigidbody rb = GetComponent<Rigidbody>();
                if (rb != null)
                    rb.linearDamping = drag;
                else
                    Debug.LogWarning($"[LuaEngine] SetDrag: No Rigidbody on {gameObject.name}");
            });

            selfTable["FreezeRotation"] = (Action<bool>)((freeze) =>
            {
                Rigidbody rb = GetComponent<Rigidbody>();
                if (rb != null)
                    rb.freezeRotation = freeze;
                else
                    Debug.LogWarning($"[LuaEngine] FreezeRotation: No Rigidbody on {gameObject.name}");
            });

            // ----- Parent -----
            selfTable["SetParent"] = (Action<string>)((parentName) =>
            {
                GameObject parent = GameObject.Find(parentName);
                if (parent != null)
                    transform.SetParent(parent.transform);
                else
                    Debug.LogWarning($"[LuaEngine] SetParent: '{parentName}' not found");
            });

            selfTable["ClearParent"] = (Action)(() =>
            {
                transform.SetParent(null);
            });

            selfTable["GetParent"] = (Func<DynValue>)(() =>
            {
                if (transform.parent != null)
                {
                    LuaEngine parentEngine = transform.parent.GetComponent<LuaEngine>();
                    if (parentEngine == null)
                        parentEngine = transform.parent.gameObject.AddComponent<LuaEngine>();
                    return DynValue.NewTable(parentEngine.CreateExternalTable(luaScript));
                }
                return DynValue.Nil;
            });

            selfTable["GetChild"] = (Func<string, DynValue>)((childName) =>
            {
                Transform child = transform.Find(childName);
                if (child != null)
                {
                    LuaEngine childEngine = child.GetComponent<LuaEngine>();
                    if (childEngine == null)
                        childEngine = child.gameObject.AddComponent<LuaEngine>();
                    return DynValue.NewTable(childEngine.CreateExternalTable(luaScript));
                }
                return DynValue.Nil;
            });

            selfTable["GetChildren"] = (Func<Table>)(() =>
            {
                Table childrenTable = new Table(luaScript);
                int index = 1;
                foreach (Transform child in transform)
                {
                    LuaEngine childEngine = child.GetComponent<LuaEngine>();
                    if (childEngine == null)
                        childEngine = child.gameObject.AddComponent<LuaEngine>();
                    childrenTable[index++] = DynValue.NewTable(childEngine.CreateExternalTable(luaScript));
                }
                return childrenTable;
            });

            selfTable["SetText"] = (Action<string>)((text) =>
            {
                // TMP_Text covers both TextMeshPro (world-space) and TextMeshProUGUI (Canvas/UI)
                var textComponent = GetComponentInChildren<TMP_Text>();
                if (textComponent != null)
                {
                    textComponent.text = text;
                }
                else
                {
                    Debug.LogWarning($"[LuaEngine] SetText: No TextMeshPro/TextMeshProUGUI component found in children of {gameObject.name}");
                }
            });

            selfTable["AppendText"] = (Action<string>)((text) =>
            {
                var textComponent = GetComponentInChildren<TMP_Text>();
                if (textComponent != null)
                {
                    textComponent.text += "\n" + text;
                }
                else
                {
                    Debug.LogWarning($"[LuaEngine] AppendText: No TextMeshPro/TextMeshProUGUI component found in children of {gameObject.name}");
                }
            });

            selfTable["ClearText"] = (Action)(() =>
            {
                var textComponent = GetComponentInChildren<TMP_Text>();
                if (textComponent != null)
                {
                    textComponent.text = "";
                }
                else
                {
                    Debug.LogWarning($"[LuaEngine] ClearText: No TextMeshPro/TextMeshProUGUI component found in children of {gameObject.name}");
                }
            });

            selfTable["SetTextColor"] = (Action<float, float, float, float>)((r, g, b, a) =>
            {
                var textComponent = GetComponentInChildren<TMP_Text>();
                if (textComponent != null)
                {
                    textComponent.color = new Color(r, g, b, a);
                }
                else
                {
                    Debug.LogWarning($"[LuaEngine] SetTextColor: No TextMeshPro/TextMeshProUGUI component found in children of {gameObject.name}");
                }
            });

            // ===== GLOBAL FUNCTIONS =====

            // Time
            luaScript.Globals["deltaTime"] = (Func<float>)(() => Time.deltaTime);
            luaScript.Globals["time"] = (Func<float>)(() => Time.time);

            // Print (alias for self:Log)
            luaScript.Globals["print"] = (Action<DynValue>)((val) =>
            {
                Debug.Log($"[Lua:{gameObject.name}] {val.ToObject()}");
            });

            // ===== COROUTINE / WAIT SUPPORT =====
            // StartCoroutine - starts a Lua coroutine
            luaScript.Globals["StartCoroutine"] = (Action<DynValue>)((func) =>
            {
                if (func.Type == DataType.Function)
                {
                    DynValue co = luaScript.CreateCoroutine(func);
                    var info = new LuaCoroutineInfo
                    {
                        coroutine = co,
                        waitUntilTime = 0f,
                        isWaiting = false
                    };
                    activeCoroutines.Add(info);

                    // Run until first yield
                    ResumeCoroutine(info);
                }
                else
                {
                    Debug.LogWarning($"[LuaEngine] StartCoroutine requires a function");
                }
            });

            // ===== MATH LIBRARY =====
            Table math = new Table(luaScript);
            luaScript.Globals["mathf"] = math;

            math["sin"] = (Func<float, float>)(Mathf.Sin);
            math["cos"] = (Func<float, float>)(Mathf.Cos);
            math["tan"] = (Func<float, float>)(Mathf.Tan);
            math["abs"] = (Func<float, float>)(Mathf.Abs);
            math["sqrt"] = (Func<float, float>)(Mathf.Sqrt);
            math["floor"] = (Func<float, float>)(Mathf.Floor);
            math["ceil"] = (Func<float, float>)(Mathf.Ceil);
            math["round"] = (Func<float, float>)(Mathf.Round);
            math["clamp"] = (Func<float, float, float, float>)(Mathf.Clamp);
            math["lerp"] = (Func<float, float, float, float>)(Mathf.Lerp);
            math["min"] = (Func<float, float, float>)(Mathf.Min);
            math["max"] = (Func<float, float, float>)(Mathf.Max);
            math["random"] = (Func<float, float, float>)((min, max) => UnityEngine.Random.Range(min, max));
            math["pi"] = Mathf.PI;

            // ===== PHYSICS LIBRARY =====
            Table physics = new Table(luaScript);
            luaScript.Globals["physics"] = physics;

            physics["SetGravity"] = (Action<float, float, float>)((x, y, z) =>
            {
                Physics.gravity = new Vector3(x, y, z);
            });

            physics["GetGravity"] = (Func<Table>)(() =>
            {
                Table grav = new Table(luaScript);
                grav["x"] = Physics.gravity.x;
                grav["y"] = Physics.gravity.y;
                grav["z"] = Physics.gravity.z;
                return grav;
            });

            physics["Raycast"] = (Func<float, float, float, float, float, float, float, Table>)((ox, oy, oz, dx, dy, dz, maxDist) =>
            {
                Ray ray = new Ray(new Vector3(ox, oy, oz), new Vector3(dx, dy, dz));
                Table result = new Table(luaScript);
                if (Physics.Raycast(ray, out RaycastHit hit, maxDist))
                {
                    result["hit"] = true;
                    result["name"] = hit.collider.gameObject.name;
                    result["x"] = hit.point.x;
                    result["y"] = hit.point.y;
                    result["z"] = hit.point.z;
                    result["distance"] = hit.distance;
                    result["normalX"] = hit.normal.x;
                    result["normalY"] = hit.normal.y;
                    result["normalZ"] = hit.normal.z;
                }
                else
                {
                    result["hit"] = false;
                }
                return result;
            });

            physics["CheckSphere"] = (Func<float, float, float, float, bool>)((x, y, z, radius) =>
            {
                return Physics.CheckSphere(new Vector3(x, y, z), radius);
            });

            // ===== REQUIRED APIS =====
#if XR_INTERACTION_TOOLKIT
            selfTable["SetGrabbable"] = (Action<bool>)((state) =>
            {
                var grabInteractable = GetComponent<XRGrabInteractable>();
                if (grabInteractable != null)
                {
                    grabInteractable.enabled = state;
                }
                else
                {
                    Debug.LogWarning($"[LuaEngine] SetGrabbable: Component missing on {gameObject.name}");
                }
            });

            selfTable["TriggerHaptic"] = (Action<float, float>)((amplitude, duration) =>
            {
                var grabInteractable = GetComponent<XRGrabInteractable>();
                if (grabInteractable != null)
                {
                    if (grabInteractable.interactorsSelecting != null)
                    {
                        foreach (var interactor in grabInteractable.interactorsSelecting)
                        {
                            if (interactor is XRBaseInputInteractor controllerInteractor)
                            {
                                controllerInteractor.SendHapticImpulse(amplitude, duration);
                            }
                        }
                    }
                }
                else
                {
                    Debug.LogWarning($"[LuaEngine] TriggerHaptic: Component missing on {gameObject.name}");
                }
            });
#endif
            selfTable["ToggleAudio"] = (Action<bool>)((state) =>
            {
                var audioSource = GetComponent<AudioSource>();
                if (audioSource != null)
                {
                    if (state)
                    {
                        audioSource.Play();
                    }
                    else
                    {
                        audioSource.Stop();
                    }
                }
                else
                {
                    Debug.LogWarning($"[LuaEngine] ToggleAudio: Component missing on {gameObject.name}");
                }
            });
        }

        private void Start()
        {
            if (!isInitialized) return;
            CallLuaFunction(onStartFunc);
        }

        private void Update()
        {
            if (!isInitialized) return;
            CallLuaFunction(onUpdateFunc);

            // Process active coroutines
            UpdateCoroutines();
        }

        private void UpdateCoroutines()
        {
            for (int i = activeCoroutines.Count - 1; i >= 0; i--)
            {
                var info = activeCoroutines[i];

                // Check if waiting
                if (info.isWaiting)
                {
                    if (Time.time >= info.waitUntilTime)
        {
                        info.isWaiting = false;
                        ResumeCoroutine(info);
                    }
                }

                // Remove completed coroutines
                if (info.coroutine.Coroutine.State == CoroutineState.Dead)
            {
                    activeCoroutines.RemoveAt(i);
                }
            }
        }

        private void ResumeCoroutine(LuaCoroutineInfo info)
        {
                try
                {
                DynValue result = info.coroutine.Coroutine.Resume();

                // Check if coroutine yielded a wait value
                if (result.Type == DataType.Number)
                {
                    float waitSeconds = (float)result.Number;
                    info.waitUntilTime = Time.time + waitSeconds;
                    info.isWaiting = true;
                }
                }
                catch (ScriptRuntimeException ex)
                {
                    Debug.LogError($"[LuaEngine] Coroutine error in {gameObject.name}: {ex.DecoratedMessage}");
                // Mark as dead by removing from list
                activeCoroutines.Remove(info);
            }
        }

        /// <summary>
        /// Call this when player interacts with the object
        /// </summary>
        public void OnPlayerInteract(string playerId = "Player")
        {
            if (!isInitialized) return;
            CallLuaFunction(onInteractFunc, playerId);
        }

        private void OnMouseDown()
        {
            // Simple click detection for testing
            OnPlayerInteract("MouseClick");
        }

        private void OnMouseOver()
        {
            // Simple activate detection for testing without VR
            // Allows pressing 'T' while looking at the object to activate it directly
            if (Input.GetKeyDown(KeyCode.T))
            {
                CallLuaCallback("OnActivate", "Keyboard");
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!isInitialized) return;
            CallLuaFunction(onTriggerEnterFunc, other.gameObject.name);
        }

        private void OnTriggerExit(Collider other)
        {
            if (!isInitialized) return;
            CallLuaFunction(onTriggerExitFunc, other.gameObject.name);
        }

        private void CallLuaFunction(DynValue func, params object[] args)
        {
            if (func == null || func.IsNil()) return;

            try
            {
                if (args.Length > 0)
                {
                    luaScript.Call(func, args);
                }
                else
                {
                    luaScript.Call(func);
                }
            }
            catch (ScriptRuntimeException ex)
            {
                Debug.LogError($"[LuaEngine] Runtime error in {gameObject.name}: {ex.DecoratedMessage}");
            }
        }

        // ===== XR EVENT HANDLERS =====

#if XR_INTERACTION_TOOLKIT
        private void OnXRSelectEntered(SelectEnterEventArgs args)
        {
            if (args.interactorObject is XRSocketInteractor)
            {
                string socketName = args.interactorObject.transform.gameObject.name;
                CallLuaCallback("OnSnap", socketName);
                return;
            }

            string hand = GetXRInteractorName(args.interactorObject);
            // Fire OnInteract for backward compatibility
            OnPlayerInteract($"VR:{hand}");
            // Fire dedicated XR callback
            CallLuaCallback("OnGrab", hand);
        }

        private void OnXRSelectExited(SelectExitEventArgs args)
        {
            if (args.interactorObject is XRSocketInteractor)
            {
                string socketName = args.interactorObject.transform.gameObject.name;
                CallLuaCallback("OnUnsnap", socketName);
                return;
            }

            string hand = GetXRInteractorName(args.interactorObject);
            CallLuaCallback("OnRelease", hand);
        }

        private void OnXRSocketSelectEntered(SelectEnterEventArgs args)
        {
            string grabbedObject = args.interactableObject.transform.gameObject.name;
            CallLuaCallback("OnSnap", grabbedObject);
        }

        private void OnXRSocketSelectExited(SelectExitEventArgs args)
        {
            string grabbedObject = args.interactableObject.transform.gameObject.name;
            CallLuaCallback("OnUnsnap", grabbedObject);
        }

        private void OnXRHoverEntered(HoverEnterEventArgs args)
        {
            string hand = GetXRInteractorName(args.interactorObject);
            CallLuaCallback("OnHoverEnter", hand);

            // Enable hover activation on the interactor so it can activate this object without grabbing
            if (args.interactorObject is XRBaseInputInteractor inputInteractor)
            {
                inputInteractor.allowHoveredActivate = true;
            }
        }

        private void OnXRHoverExited(HoverExitEventArgs args)
        {
            string hand = GetXRInteractorName(args.interactorObject);
            CallLuaCallback("OnHoverExit", hand);
        }

        private void OnXRActivated(ActivateEventArgs args)
        {
            string hand = GetXRInteractorName(args.interactorObject);
            CallLuaCallback("OnActivate", hand);
        }

        private void OnXRDeactivated(DeactivateEventArgs args)
        {
            string hand = GetXRInteractorName(args.interactorObject);
            CallLuaCallback("OnDeactivate", hand);
        }

        private string GetXRInteractorName(IXRInteractor interactor)
        {
            if (interactor is MonoBehaviour mb)
            {
                string name = mb.gameObject.name.ToLower();
                if (name.Contains("left")) return "LeftHand";
                if (name.Contains("right")) return "RightHand";
                return mb.gameObject.name;
            }
            return "Unknown";
        }
#endif

        /// <summary>
        /// Call a Lua callback by name. Used internally for XR events.
        /// </summary>
        public void CallLuaCallback(string callbackName, params object[] args)
        {
            if (!isInitialized) return;

            DynValue func = callbackName switch
            {
                "OnGrab" => onGrabFunc,
                "OnRelease" => onReleaseFunc,
                "OnHoverEnter" => onHoverEnterFunc,
                "OnHoverExit" => onHoverExitFunc,
                "OnActivate" => onActivateFunc,
                "OnDeactivate" => onDeactivateFunc,
                "OnSnap" => onSnapFunc,
                "OnUnsnap" => onUnsnapFunc,
                _ => luaScript.Globals.Get(callbackName)
            };

            CallLuaFunction(func, args);
        }
    }
}
