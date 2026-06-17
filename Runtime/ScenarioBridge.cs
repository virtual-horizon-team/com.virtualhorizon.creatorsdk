#if !UNITY_6000_0_OR_NEWER
#error Creator SDK requires Unity 6 (6000.x) or newer. Please upgrade your Unity version.
#endif

using UnityEngine;
using System;
using System.Collections.Generic;

namespace CreatorSDK
{
    /// <summary>
    /// Bridge component that connects prefabs to Lua scripts at runtime.
    /// Attach this to any prefab that needs runtime behavior.
    /// </summary>
    [AddComponentMenu("Creator SDK/Scenario Bridge")]
    public class ScenarioBridge : MonoBehaviour
    {
        [Header("Lua Script")]
        [Tooltip("The Lua script that defines this object's behavior")]
        public TextAsset luaScript;
        
        [Header("Exposed Properties")]
        [Tooltip("Properties that users can modify at runtime")]
        public List<ExposedProperty> exposedProperties = new List<ExposedProperty>();
        
        [Header("Interaction Settings")]
        public bool isInteractable = true;
        public float interactionRange = 3f;
        public string interactionPrompt = "Press E to interact";
        
        [Header("XR Settings")]
        [Tooltip("If true, this object can be grabbed in XR")]
        public bool isGrabbable = false;
        
        [Tooltip("If true, this object acts as a receiver socket for grabbable objects")]
        public bool isSnapZone = false;
        
        [Tooltip("Optional: Custom transform point for grabbing or snapping")]
        public Transform customAttachPoint;
        
        [Tooltip("Optional: Tag string. Sockets will only accept grabbables with the same tag.")]
        public string snapTag = "";
        
        [Header("Placement Rules")]
        public bool canRotate = true;
        public bool canScale = true;
        public Vector3 minScale = Vector3.one * 0.1f;
        public Vector3 maxScale = Vector3.one * 10f;
        public bool snapToGround = true;
        
        // Runtime state (set by the host game's Lua VM)
        [NonSerialized] public object luaInstance;
        [NonSerialized] public Action<string, object> onPropertyChanged;
        
        // Events that Lua can hook into
        public event Action OnStartEvent;
        public event Action<float> OnUpdateEvent;
        public event Action<string> OnInteractEvent;
        public event Action<GameObject> OnTriggerEnterEvent;
        public event Action<GameObject> OnTriggerExitEvent;

        private Dictionary<string, object> propertyValues = new Dictionary<string, object>();

        private void Start()
        {
            // Initialize default property values
            foreach (var prop in exposedProperties)
            {
                propertyValues[prop.propertyName] = prop.GetDefaultValue();
            }
            
            // Notify Lua
            OnStartEvent?.Invoke();
        }

        private void Update()
        {
            OnUpdateEvent?.Invoke(Time.deltaTime);
        }

        private void OnTriggerEnter(Collider other)
        {
            OnTriggerEnterEvent?.Invoke(other.gameObject);
        }

        private void OnTriggerExit(Collider other)
        {
            OnTriggerExitEvent?.Invoke(other.gameObject);
        }

        /// <summary>
        /// Called when user interacts with this object
        /// </summary>
        public void Interact(string userId)
        {
            if (!isInteractable) return;
            OnInteractEvent?.Invoke(userId);
        }

        /// <summary>
        /// Get a property value
        /// </summary>
        public T GetProperty<T>(string propertyName)
        {
            if (propertyValues.TryGetValue(propertyName, out var value))
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            return default;
        }

        /// <summary>
        /// Set a property value
        /// </summary>
        public void SetProperty(string propertyName, object value)
        {
            propertyValues[propertyName] = value;
            onPropertyChanged?.Invoke(propertyName, value);
            
            // Handle built-in properties
            ApplyBuiltInProperty(propertyName, value);
        }

        private void ApplyBuiltInProperty(string name, object value)
        {
            switch (name.ToLower())
            {
                case "color":
                    if (value is Color color)
                    {
                        foreach (var renderer in GetComponentsInChildren<Renderer>())
                        {
                            var mpb = new MaterialPropertyBlock();
                            renderer.GetPropertyBlock(mpb);
                            mpb.SetColor("_Color", color);
                            renderer.SetPropertyBlock(mpb);
                        }
                    }
                    break;
                    
                case "visible":
                    if (value is bool visible)
                    {
                        foreach (var renderer in GetComponentsInChildren<Renderer>())
                        {
                            renderer.enabled = visible;
                        }
                    }
                    break;
                    
                case "scale":
                    if (value is float scale)
                    {
                        transform.localScale = Vector3.one * Mathf.Clamp(scale, minScale.x, maxScale.x);
                    }
                    break;
            }
        }

        #region Lua API Methods (Called from Lua scripts)

        public void SetPosition(float x, float y, float z)
        {
            transform.position = new Vector3(x, y, z);
        }

        public void SetRotation(float x, float y, float z)
        {
            transform.eulerAngles = new Vector3(x, y, z);
        }

        public void SetScale(float x, float y, float z)
        {
            Vector3 newScale = new Vector3(
                Mathf.Clamp(x, minScale.x, maxScale.x),
                Mathf.Clamp(y, minScale.y, maxScale.y),
                Mathf.Clamp(z, minScale.z, maxScale.z)
            );
            transform.localScale = newScale;
        }

        public void SetColor(float r, float g, float b, float a = 1f)
        {
            SetProperty("color", new Color(r, g, b, a));
        }

        public void PlayAnimation(string animationName)
        {
            var animator = GetComponent<Animator>();
            if (animator != null)
            {
                animator.Play(animationName);
            }
        }

        public void SetAnimatorBool(string paramName, bool value)
        {
            var animator = GetComponent<Animator>();
            if (animator != null)
            {
                animator.SetBool(paramName, value);
            }
        }

        public void SetAnimatorTrigger(string triggerName)
        {
            var animator = GetComponent<Animator>();
            if (animator != null)
            {
                animator.SetTrigger(triggerName);
            }
        }

        public void Log(string message)
        {
            Debug.Log($"[{gameObject.name}] {message}");
        }

        public void Rotate(float x, float y, float z)
        {
            if (!canRotate) return;
            transform.Rotate(x, y, z);
        }

        public void Move(float x, float y, float z)
        {
            transform.Translate(x, y, z);
        }

        public void LookAt(float x, float y, float z)
        {
            transform.LookAt(new Vector3(x, y, z));
        }

        public void SetActive(bool active)
        {
            gameObject.SetActive(active);
        }

        public void Destroy()
        {
            Destroy(gameObject);
        }

        public Vector3 GetPosition()
        {
            return transform.position;
        }

        public Vector3 GetRotation()
        {
            return transform.eulerAngles;
        }

        public Vector3 GetScale()
        {
            return transform.localScale;
        }

        #endregion

        #region Serialization (for saving/loading scenarios)

        public ScenarioBridgeData Serialize()
        {
            return new ScenarioBridgeData
            {
                position = transform.position,
                rotation = transform.eulerAngles,
                scale = transform.localScale,
                properties = new Dictionary<string, object>(propertyValues)
            };
        }

        public void Deserialize(ScenarioBridgeData data)
        {
            transform.position = data.position;
            transform.eulerAngles = data.rotation;
            transform.localScale = data.scale;
            
            if (data.properties != null)
            {
                foreach (var kvp in data.properties)
                {
                    SetProperty(kvp.Key, kvp.Value);
                }
            }
        }

        #endregion
    }

    #region Data Classes

    [Serializable]
    public class ExposedProperty
    {
        public string propertyName;
        public string displayName;
        public PropertyType type;
        public string defaultValue;
        public string minValue;
        public string maxValue;
        public string[] dropdownOptions;

        public object GetDefaultValue()
        {
            return type switch
            {
                PropertyType.Float => float.TryParse(defaultValue, out var f) ? f : 0f,
                PropertyType.Int => int.TryParse(defaultValue, out var i) ? i : 0,
                PropertyType.Bool => bool.TryParse(defaultValue, out var b) && b,
                PropertyType.Color => ColorUtility.TryParseHtmlString(defaultValue, out var c) ? c : Color.white,
                PropertyType.String => defaultValue ?? "",
                PropertyType.Dropdown => defaultValue ?? "",
                _ => defaultValue
            };
        }
    }

    public enum PropertyType
    {
        Float,
        Int,
        Bool,
        String,
        Color,
        Dropdown
    }

    [Serializable]
    public class ScenarioBridgeData
    {
        public Vector3 position;
        public Vector3 rotation;
        public Vector3 scale;
        public Dictionary<string, object> properties;
    }

    #endregion
}
