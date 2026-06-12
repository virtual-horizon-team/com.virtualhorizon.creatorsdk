# Creator SDK

A complete toolkit for creating uploadable assets with runtime behavior for the Scenario platform.

## Quick Start

### 1. Open the Creator SDK Window
- Go to **Creator SDK → Asset Uploader** (or press `Ctrl+Shift+U`)

### 2. Create Your Prefab
1. Create your 3D model in Unity
2. Add the **ScenarioBridge** component (`Add Component → Creator SDK → Scenario Bridge`)
3. Create a Lua script for custom behavior

### 3. Add Lua Behavior
- In the ScenarioBridge inspector, click **Create New Script**
- Or choose from templates: Interactable, Rotating, Trigger Zone, etc.
- Edit the Lua script to define your object's behavior

### 4. Export for Upload
1. Drag your prefab into the Creator SDK window
2. Fill in metadata (name, description, category, tags)
3. Click **Validate All** to check for issues
4. Click **Export Packages** to generate uploadable files

---

## Components

### ScenarioBridge
The main component that connects your prefab to Lua scripts at runtime.

**Features:**
- Lua script attachment
- Exposed properties (user-configurable at runtime)
- Interaction settings
- Placement rules

---

## Lua API Reference

### Lifecycle Functions

```lua
-- Called when object is spawned
function OnStart()
end

-- Called every frame
function OnUpdate(deltaTime)
end
```

### Interaction Functions

```lua
-- Called when user clicks/interacts
function OnInteract(userId)
end

-- Called when another object enters trigger
function OnTriggerEnter(other)
end

-- Called when another object exits trigger
function OnTriggerExit(other)
end

-- Called when a property changes
function OnPropertyChanged(name, value)
end
```

### Transform Commands

```lua
self:SetPosition(x, y, z)
self:SetRotation(x, y, z)
self:SetScale(x, y, z)
self:Move(x, y, z)        -- Relative movement
self:Rotate(x, y, z)      -- Relative rotation
self:LookAt(x, y, z)      -- Face a point

-- Get current values
local pos = self:GetPosition()  -- Returns {x, y, z}
local rot = self:GetRotation()
local scale = self:GetScale()
```

### Visual Commands

```lua
self:SetColor(r, g, b, a)       -- Set material color
self:SetActive(true/false)       -- Show/hide object
self:PlayAnimation("AnimName")   -- Play animation
self:SetAnimatorBool("param", true)
self:SetAnimatorTrigger("trigger")
```

### Utility

```lua
self:Log("message")              -- Debug log
self:Destroy()                   -- Remove object
```

---

## Sample Scripts

Located in `CreatorSDK/Samples/Scripts/`:

| Script | Description |
|--------|-------------|
| `InteractiveButton.lua.txt` | Toggle button with color feedback |
| `SpinningDisplay.lua.txt` | Rotating showcase with bobbing |
| `ProximityTrigger.lua.txt` | Detects objects in an area |
| `TimerDisplay.lua.txt` | Countdown/countup timer |
| `CollectibleItem.lua.txt` | Collectable with respawn |

---

## Validation Rules

The SDK validates prefabs before export:

### Errors (Must Fix)
- Missing script references
- Polygon count > 100,000
- Texture size > 4096px
- Forbidden Lua API calls (os.*, io.*, require)

### Warnings
- Custom C# scripts (won't run at runtime)
- High polygon count (> 50,000)
- Large textures (> 2048px)
- No colliders on interactable objects

---

## Export Package Contents

Each exported `.unitypackage` contains:
1. **{GUID}.prefab** - The renamed prefab
2. **{GUID}_metadata.json** - Asset metadata
3. **{GUID}.lua.txt** - The Lua script (if attached)
4. All dependencies (materials, textures, etc.)

---

## Tips

1. **Keep polygon count low** - Target < 10,000 vertices for mobile
2. **Use power-of-2 textures** - 256, 512, 1024, 2048
3. **Add colliders** - Required for interactions
4. **Test Lua scripts locally** - Check the console for errors
5. **Use exposed properties** - Let users customize without editing scripts

---

## Folder Structure

```
Assets/CreatorSDK/
├── Editor/
│   ├── CreatorSDKWindow.cs      # Main uploader UI
│   ├── ScenarioBridgeEditor.cs  # Custom inspector
│   ├── AssetValidator.cs        # Validation system
│   └── LuaTemplates.cs          # Script templates
├── Runtime/
│   └── ScenarioBridge.cs        # Runtime component
└── Samples/
    └── Scripts/                 # Sample Lua scripts
```

---

## Support

For issues or feature requests, contact the development team.
