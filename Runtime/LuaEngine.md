# LuaEngine — Complete Documentation

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Lifecycle](#lifecycle)
4. [XR Integration](#xr-integration)
5. [Lua API Reference](#lua-api-reference)
   - [Lifecycle Callbacks](#lifecycle-callbacks)
   - [XR Lifecycle Callbacks](#xr-lifecycle-callbacks)
   - [self — Transform](#self--transform)
   - [self — Visual & Animation](#self--visual--animation)
   - [self — Light](#self--light)
   - [self — Audio](#self--audio)
   - [self — Particles](#self--particles)
   - [self — Input](#self--input)
   - [self — Utility](#self--utility)
   - [self — Hierarchy](#self--hierarchy)
   - [self — Inter-Object Communication](#self--inter-object-communication)
   - [self — Physics & Rigidbody](#self--physics--rigidbody)
   - [self — XR API](#self--xr-api)
   - [Global Functions](#global-functions)
   - [Coroutines & Wait](#coroutines--wait)
   - [Math Library (mathf)](#math-library-mathf)
   - [Physics Library (physics)](#physics-library-physics)
6. [Scripting Examples](#scripting-examples)
7. [What You CAN Write](#what-you-can-write)
8. [Limitations](#limitations)

---

## Overview

`LuaEngine` is the runtime Lua execution engine for the **Creator SDK**. It is built on top of [MoonSharp](https://www.moonsharp.org/) (a pure C# Lua interpreter) and runs sandboxed Lua scripts attached to Unity GameObjects.

Every GameObject that needs scripted behavior gets two components:

| Component | Role |
|---|---|
| **ScenarioBridge** | Holds the Lua `TextAsset`, exposed properties, interaction/placement settings, and provides C#-side events. |
| **LuaEngine** | Creates a MoonSharp `Script` environment, exposes a safe API surface to Lua, executes the script, and dispatches Unity lifecycle events into Lua callbacks. |

`LuaEngine` requires `ScenarioBridge` (enforced via `[RequireComponent]`).

> **Script format:** Lua scripts must be saved as `.lua.txt` text assets (Unity requires the `.txt` extension for `TextAsset`). Assign the file to the `luaScript` field on `ScenarioBridge`.

---

## ⚡ Quick Start — Write Your First Script

Create a file named `MyObject.lua.txt`. A script can be as simple as this:

```lua
-- Runs once when the scene starts
function OnStart()
    self:Log("Hello from " .. self:GetName())
end

-- Runs every frame
function OnUpdate()
    self:Rotate(0, 45 * deltaTime(), 0)   -- spin slowly
end

-- Runs when a player clicks or interacts
function OnInteract(userId)
    self:SetColor(0, 1, 0, 1)   -- turn green
    self:Log(userId .. " interacted!")
end
```

**Rules to remember:**
- Always use `self:FunctionName(...)` — the colon `:` is required.
- `deltaTime()` returns seconds since last frame — multiply speeds by it for frame-rate independence.
- All color values are `0..1` (not `0..255`). Full opacity = alpha `1`.
- You only need to define the callbacks you actually use — skip any you don't need.
- `print(value)` and `self:Log(message)` both write to the Unity Console.

---

## Architecture

```
┌──────────────────────────────────────────────────┐
│  Unity Runtime                                   │
│                                                  │
│  ┌────────────┐        ┌─────────────────────┐   │
│  │ ScenarioBridge │──►│     LuaEngine        │   │
│  │  (TextAsset)   │    │  (MoonSharp Script) │   │
│  └────────────┘        └──────┬──────────────┘   │
│                               │                  │
│                   ┌───────────▼──────────┐       │
│                   │  Lua Script (.lua.txt)│       │
│                   │                      │       │
│                   │  OnStart()           │       │
│                   │  OnUpdate()          │       │
│                   │  OnInteract(userId)  │       │
│                   │  OnTriggerEnter(name)│       │
│                   │  OnTriggerExit(name) │       │
│                   └──────────────────────┘       │
└──────────────────────────────────────────────────┘
```

### Initialization Flow

1. **Awake()** — `LuaEngine` gets the sibling `ScenarioBridge`, registers itself in a static `Dictionary<string, LuaEngine>` keyed by `gameObject.name`, then calls `InitializeLua()`.
2. **InitializeLua()** — Creates a fresh MoonSharp `Script`, calls `RegisterAPI()` to bind all C# functions/tables into the Lua global scope, then executes the Lua source code with `DoString()`. After execution it caches `DynValue` references to `OnStart`, `OnUpdate`, `OnInteract`, `OnTriggerEnter`, `OnTriggerExit`.
3. **Start()** — Calls the Lua `OnStart()` function (if defined).
4. **Update()** — Calls the Lua `OnUpdate()` function every frame, then ticks all active Lua coroutines.

### Object Registry

All `LuaEngine` instances register themselves in a **static dictionary** keyed by `gameObject.name`. This allows any Lua script to look up another scripted object by name via `self:FindObject("OtherName")`.

---

## Lifecycle

| Unity Event | Lua Callback Called | Argument |
|---|---|---|
| `Start()` | `OnStart()` | — |
| `Update()` | `OnUpdate()` | — |
| `OnMouseDown()` | `OnInteract(userId)` | `"MouseClick"` |
| `OnPlayerInteract(id)` (public C# method) | `OnInteract(userId)` | the provided `playerId` string |
| `OnTriggerEnter(Collider)` | `OnTriggerEnter(name)` | `other.gameObject.name` |
| `OnTriggerExit(Collider)` | `OnTriggerExit(name)` | `other.gameObject.name` |
| XR Select Entered | `OnGrab(hand)` | `"LeftHand"` / `"RightHand"` |
| XR Select Exited | `OnRelease(hand)` | `"LeftHand"` / `"RightHand"` |
| XR Hover Entered | `OnHoverEnter(hand)` | `"LeftHand"` / `"RightHand"` |
| XR Hover Exited | `OnHoverExit(hand)` | `"LeftHand"` / `"RightHand"` |
| XR Activated | `OnActivate(hand)` | `"LeftHand"` / `"RightHand"` |

> **Note:** `OnUpdate()` receives **no argument** in this version. Use the global `deltaTime()` function instead.
>
> **Note:** XR callbacks are only available when the **XR Interaction Toolkit** package is installed. The `hand` argument is `"LeftHand"`, `"RightHand"`, or the interactor GameObject name.

---

## XR Integration

When the **XR Interaction Toolkit** package is installed (`XR_INTERACTION_TOOLKIT` define), `LuaEngine` automatically enables VR/AR interaction support on every scripted object:

### Auto-Setup

1. If the GameObject has no `XRBaseInteractable`, an `XRSimpleInteractable` is added automatically.
2. If the GameObject has no `Collider`, a `BoxCollider` is added automatically.
3. All XR interaction events are wired to Lua callbacks.

### XR → Lua Event Flow

```
XR Controller → XRBaseInteractable → LuaEngine → Lua Callback
```

- **Grab (Select)** also fires `OnInteract("VR:LeftHand")` or `OnInteract("VR:RightHand")` for backward compatibility with non-XR scripts.
- **Hand detection** is based on the interactor GameObject name: if it contains `"left"` → `"LeftHand"`, `"right"` → `"RightHand"`, otherwise the raw name is passed.

### Supported XR Interactions

| XR Event | Lua Callback | Typical Use |
|---|---|---|
| Select Entered (grab) | `OnGrab(hand)` | Pick up objects, grab levers |
| Select Exited (release) | `OnRelease(hand)` | Drop objects, release levers |
| Hover Entered | `OnHoverEnter(hand)` | Highlight on gaze/point |
| Hover Exited | `OnHoverExit(hand)` | Remove highlight |
| Activated (trigger press) | `OnActivate(hand)` | Fire, use, activate while holding |

---

## Lua API Reference

### Lifecycle Callbacks

Define these **global functions** in your Lua script. They are optional — only define the ones you need.

```lua
function OnStart()
    -- Runs once when the object spawns
end

function OnUpdate()
    -- Runs every frame
    local dt = deltaTime()
end

function OnInteract(userId)
    -- Runs when a player interacts or clicks
end

function OnTriggerEnter(otherName)
    -- Runs when another collider enters this object's trigger
end

function OnTriggerExit(otherName)
    -- Runs when another collider exits this object's trigger
end
```

---

### XR Lifecycle Callbacks

These callbacks are available when the **XR Interaction Toolkit** is installed. Define them as global functions — only define the ones you need.

```lua
function OnGrab(hand)
    -- Runs when a VR controller grabs this object
    self:Log("Grabbed by " .. hand)
end

function OnRelease(hand)
    -- Runs when a VR controller releases this object
    self:Log("Released by " .. hand)
end

function OnHoverEnter(hand)
    -- Runs when a VR controller starts pointing at / hovering over this object
    self:SetColor(1, 1, 0, 1)  -- highlight yellow
end

function OnHoverExit(hand)
    -- Runs when a VR controller stops hovering over this object
    self:SetColor(1, 1, 1, 1)  -- reset to white
end

function OnActivate(hand)
    -- Runs when the trigger button is pressed while holding this object
    self:Log("Activated by " .. hand)
end
```

> The `hand` parameter is `"LeftHand"`, `"RightHand"`, or the interactor name.
>
> **Backward compatibility:** `OnGrab` also fires `OnInteract("VR:LeftHand")` / `OnInteract("VR:RightHand")`, so non-XR scripts continue to work.

---

### self — Transform

The `self` table represents the current GameObject.

| Function | Signature | Description |
|---|---|---|
| `self:SetPosition(x, y, z)` | `(float, float, float) → void` | Set world position |
| `self:GetPosition()` | `() → {x, y, z}` | Get world position as a table |
| `self:SetLocalPosition(x, y, z)` | `(float, float, float) → void` | Set **local** position relative to parent |
| `self:GetLocalPosition()` | `() → {x, y, z}` | Get local position as a table |
| `self:SetRotation(x, y, z)` | `(float, float, float) → void` | Set world euler angles |
| `self:GetRotation()` | `() → {x, y, z}` | Get world euler angles as a table |
| `self:SetScale(x, y, z)` | `(float, float, float) → void` | Set local scale |
| `self:GetScale()` | `() → {x, y, z}` | Get local scale as a table |
| `self:Move(x, y, z)` | `(float, float, float) → void` | Translate in **local** space |
| `self:Rotate(x, y, z)` | `(float, float, float) → void` | Rotate in **local** space |
| `self:LookAt(x, y, z)` | `(float, float, float) → void` | Face a world-space point |
| `self:RotateAround(px, py, pz, ax, ay, az, angle)` | `(7× float) → void` | Rotate around a world point `(px,py,pz)` on axis `(ax,ay,az)` by `angle` degrees |

---

### self — Visual & Animation

| Function | Signature | Description |
|---|---|---|
| `self:SetColor(r, g, b, a)` | `(float, float, float, float) → void` | Set material color (0–1 range) |
| `self:SetActive(bool)` | `(bool) → void` | Show/hide the entire GameObject |
| `self:PlayAnimation(name)` | `(string) → void` | Play an Animator state by name |

---

### self — Light

These functions operate on the **Light** component attached to the current GameObject. If no Light is present, a warning is logged and the call is ignored.

| Function | Signature | Description |
|---|---|---|
| `self:SetLightEnabled(bool)` | `(bool) → void` | Enable/disable the Light component |
| `self:SetLightColor(r, g, b)` | `(float, float, float) → void` | Set light color |
| `self:SetLightIntensity(intensity)` | `(float) → void` | Set light intensity (clamped to `>= 0`) |
| `self:SetLightRange(range)` | `(float) → void` | Set light range (clamped to `>= 0`) |

```lua
-- Flicker a light between dim and bright
function OnStart()
    self:SetLightEnabled(true)
    self:SetLightColor(1, 0.6, 0.1)   -- warm orange
    StartCoroutine(Flicker)
end

function Flicker()
    while true do
        self:SetLightIntensity(mathf.random(0.5, 2.0))
        coroutine.yield(wait(0.1))
    end
end
```

---

### self — Audio

These functions operate on the **AudioSource** component attached to the current GameObject. If no AudioSource is present, a warning is logged and the call is ignored.

| Function | Signature | Description |
|---|---|---|
| `self:PlayAudio()` | `() → void` | Play attached AudioSource |
| `self:PauseAudio()` | `() → void` | Pause attached AudioSource |
| `self:StopAudio()` | `() → void` | Stop attached AudioSource |
| `self:ToggleAudio(state)` | `(bool) → void` | `true` = Play, `false` = Stop. Convenience wrapper |
| `self:SetAudioVolume(volume)` | `(float) → void` | Set volume (clamped to `0..1`) |
| `self:SetAudioPitch(pitch)` | `(float) → void` | Set pitch (clamped to `-3..3`) |
| `self:SetAudioLoop(bool)` | `(bool) → void` | Enable/disable looping |
| `self:SetAudioMute(bool)` | `(bool) → void` | Mute/unmute |
| `self:IsAudioPlaying()` | `() → bool` | Returns whether audio is currently playing |

```lua
-- Play audio on interact, stop it if already playing
function OnInteract(userId)
    if self:IsAudioPlaying() then
        self:StopAudio()
    else
        self:SetAudioVolume(0.8)
        self:PlayAudio()
    end
end
```

---

### self — Particles

These functions operate on a cached registry of **all ParticleSystem components on this GameObject and its children**.

Registry key priority:
1. `ParticleTag.Name` (if `ParticleTag` exists on the same object as the ParticleSystem)
2. Particle GameObject name
3. Auto fallback key (`Particle_#`)

`default` is always mapped to the first discovered ParticleSystem.

Use `self:RefreshParticles()` if particles are added/removed at runtime.

| Function | Signature | Description |
|---|---|---|
| `self:RefreshParticles()` | `() → void` | Rebuild cached particle registry |
| `self:PlayParticle(name)` | `(string) → void` | Play one particle by key |
| `self:PauseParticle(name)` | `(string) → void` | Pause one particle by key |
| `self:StopParticle(name, clear)` | `(string, bool) → void` | Stop one particle by key |
| `self:ClearParticle(name)` | `(string) → void` | Clear one particle by key |
| `self:SetParticleLoop(name, loop)` | `(string, bool) → void` | Toggle looping for one particle by key |
| `self:IsPlaying(name)` | `(string) → bool` | Check if one particle is playing |

Group control (optional, via `ParticleTag.Group`):

| Function | Signature | Description |
|---|---|---|
| `self:PlayGroup(groupName)` | `(string) → void` | Play all particles in a group |
| `self:PauseGroup(groupName)` | `(string) → void` | Pause all particles in a group |
| `self:StopGroup(groupName, clear)` | `(string, bool) → void` | Stop all particles in a group |
| `self:ClearGroup(groupName)` | `(string) → void` | Clear all particles in a group |
| `self:IsGroupPlaying(groupName)` | `(string) → bool` | `true` if any particle in group is playing |

Backward compatibility aliases (single particle, key = `default`):

| Function | Signature | Description |
|---|---|---|
| `self:PlayParticles()` | `() → void` | Alias to `PlayParticle("default")` |
| `self:PauseParticles()` | `() → void` | Alias to `PauseParticle("default")` |
| `self:StopParticles(clear)` | `(bool) → void` | Alias to `StopParticle("default", clear)` |
| `self:ClearParticles()` | `() → void` | Alias to `ClearParticle("default")` |
| `self:SetParticlesLoop(bool)` | `(bool) → void` | Alias to `SetParticleLoop("default", bool)` |
| `self:IsParticlesPlaying()` | `() → bool` | Alias to `IsPlaying("default")` |

```lua
-- Play the default (first) particle on interact, stop it on next interact
local playing = false

function OnInteract(userId)
    if playing then
        self:StopParticles(true)   -- stop and clear
        playing = false
    else
        self:PlayParticles()
        playing = true
    end
end

-- Named particle example (particle GameObject is named "Sparks")
function OnStart()
    self:PlayParticle("Sparks")
end
```

---

### self — Input

| Function | Signature | Description |
|---|---|---|
| `self:GetKey(keyName)` | `(string) → bool` | `true` while the key is held. Uses Unity `KeyCode` names (e.g. `"W"`, `"Space"`, `"LeftShift"`) |
| `self:GetKeyDown(keyName)` | `(string) → bool` | `true` on the frame the key is pressed |
| `self:GetKeyUp(keyName)` | `(string) → bool` | `true` on the frame the key is released |
| `self:GetMouseButton(button)` | `(int) → bool` | `true` while mouse button is held (`0` = left, `1` = right, `2` = middle) |
| `self:GetMouseButtonDown(button)` | `(int) → bool` | `true` on the frame the mouse button is pressed |
| `self:GetMouseButtonUp(button)` | `(int) → bool` | `true` on the frame the mouse button is released |

---

### self — Utility

| Function | Signature | Description |
|---|---|---|
| `self:Log(message)` | `(string) → void` | Print to Unity console prefixed with `[Lua:<ObjectName>]` |
| `self:GetName()` | `() → string` | Return the GameObject's name |
| `self:SetActive(bool)` | `(bool) → void` | Show (`true`) or hide (`false`) the entire GameObject |
| `self:SetText(text)` | `(string) → void` | Set the text of a **TextMeshPro** component on this object or its children |
| `self:Destroy()` | `() → void` | Destroy this GameObject |

> **Note:** `SetParent` and `ClearParent` are also listed here for convenience but are documented fully in [self — Hierarchy](#self--hierarchy).

---

### self — Hierarchy

These functions let you navigate the GameObject hierarchy from Lua. `GetParent`, `GetChild`, and `GetChildren` return a **proxy `self` table** for the target object, so you can call any `self` function on the result.

| Function | Signature | Description |
|---|---|---|
| `self:GetParent()` | `() → table \| nil` | Returns the parent GameObject's self table, or `nil` if none |
| `self:GetChild(name)` | `(string) → table \| nil` | Returns a named direct child's self table, or `nil` if not found |
| `self:GetChildren()` | `() → table[]` | Returns an array of self tables for all direct children |
| `self:SetParent(parentName)` | `(string) → void` | Re-parent this object to a scene object found by name |
| `self:ClearParent()` | `() → void` | Detach from parent (makes this a root object) |

```lua
-- Walk up to parent and log its name
local parent = self:GetParent()
if parent then
    self:Log("My parent is: " .. parent:GetName())
end

-- Control a named child
local barrel = self:GetChild("Barrel")
if barrel then
    barrel:SetActive(false)
end

-- Loop all children
local kids = self:GetChildren()
for i = 1, #kids do
    kids[i]:SetColor(1, 0, 0, 1)
end
```

---

### self — Inter-Object Communication

| Function | Signature | Description |
|---|---|---|
| `self:FindObject(name)` | `(string) → table \| nil` | Finds another `LuaEngine`-powered object by name and returns its `self` table. Returns `nil` if not found. |

Once you have a reference, you can call any `self` function on it:

```lua
local door = self:FindObject("Door")
if door then
    door:SetPosition(0, 5, 0)
    door:SetColor(0, 1, 0, 1)
end
```

---

### self — Physics & Rigidbody

These functions operate on the **Rigidbody** component attached to the current GameObject. If no Rigidbody is present, a warning is logged and the call is ignored.

| Function | Signature | Description |
|---|---|---|
| `self:AddForce(x, y, z)` | `(float, float, float) → void` | Apply a continuous force (use in `OnUpdate`) |
| `self:AddImpulse(x, y, z)` | `(float, float, float) → void` | Apply an instant impulse (e.g. jump, explosion) |
| `self:SetVelocity(x, y, z)` | `(float, float, float) → void` | Set the linear velocity directly |
| `self:GetVelocity()` | `() → {x, y, z}` | Get the current linear velocity as a table |
| `self:SetKinematic(bool)` | `(bool) → void` | Enable/disable kinematic mode |
| `self:AddTorque(x, y, z)` | `(float, float, float) → void` | Apply rotational torque |
| `self:GetMass()` | `() → float` | Get the Rigidbody mass |
| `self:SetMass(mass)` | `(float) → void` | Set the Rigidbody mass |
| `self:SetDrag(drag)` | `(float) → void` | Set linear damping (drag) |
| `self:FreezeRotation(bool)` | `(bool) → void` | Freeze/unfreeze all rotation axes |

> **Prerequisite:** The GameObject must have a **Rigidbody** component. Add one in Unity's Inspector before using these functions.

---

### self — XR API

> ⚠️ These functions are only available when the **XR Interaction Toolkit** package is installed (`com.unity.xr.interaction.toolkit`). They silently compile away on non-XR builds.

| Function | Signature | Description |
|---|---|---|
| `self:SetGrabbable(state)` | `(bool) → void` | Enable (`true`) or disable (`false`) the `XRGrabInteractable` component. Requires `XRGrabInteractable` on the object. |
| `self:TriggerHaptic(amplitude, duration)` | `(float, float) → void` | Send a haptic impulse to all XR controllers currently holding this object. `amplitude` is 0–1, `duration` is seconds. Requires `XRGrabInteractable`. |
| `self:ToggleAudio(state)` | `(bool) → void` | `true` calls `AudioSource.Play()`, `false` calls `AudioSource.Stop()`. Requires `AudioSource`. |

```lua
-- Example: Haptic feedback + disable grabbing after first use
function OnGrab(hand)
    self:Log("Grabbed by " .. hand)
    self:TriggerHaptic(0.8, 0.2)   -- amplitude 0..1, duration in seconds
    self:SetGrabbable(false)        -- prevent grabbing again
end

-- Example: Toggle audio on grab / release
function OnGrab(hand)
    self:ToggleAudio(true)    -- start playing when picked up
end

function OnRelease(hand)
    self:ToggleAudio(false)   -- stop when dropped
end

-- Example: Re-enable grabbing after 3 seconds
function OnGrab(hand)
    self:SetGrabbable(false)
    StartCoroutine(function()
        coroutine.yield(wait(3.0))
        self:SetGrabbable(true)
    end)
end
```

---

### Global Functions

| Function | Signature | Description |
|---|---|---|
| `deltaTime()` | `() → float` | Returns `Time.deltaTime` (seconds since last frame) |
| `time()` | `() → float` | Returns `Time.time` (seconds since game start) |
| `print(value)` | `(any) → void` | Print any value to the Unity console |
| `StartCoroutine(func)` | `(function) → void` | Start a Lua coroutine (see below) |
| `wait(seconds)` | `(float) → number` | Yield a coroutine for `seconds` seconds (only valid inside a coroutine) |

---

### Coroutines & Wait

You can write timed sequences using `StartCoroutine` and `wait`:

```lua
function OnStart()
    StartCoroutine(MySequence)
end

function MySequence()
    self:Log("Step 1")
    coroutine.yield(wait(2.0))   -- pause for 2 seconds

    self:Log("Step 2")
    self:SetColor(1, 0, 0, 1)
    coroutine.yield(wait(1.5))   -- pause for 1.5 seconds

    self:Log("Step 3")
    self:SetColor(0, 1, 0, 1)
end
```

**How it works internally:**
1. `StartCoroutine(func)` wraps `func` in a MoonSharp coroutine and resumes it immediately until the first `yield`.
2. When the coroutine yields a number (from `wait(n)`), the engine stores a `waitUntilTime = Time.time + n`.
3. Each `Update()`, the engine checks if `Time.time >= waitUntilTime` and resumes the coroutine.
4. When the coroutine finishes (state = `Dead`), it is automatically removed.

---

### Math Library (mathf)

All functions are under the `mathf` global table.

| Function | Signature | Description |
|---|---|---|
| `mathf.sin(x)` | `(float) → float` | Sine (radians) |
| `mathf.cos(x)` | `(float) → float` | Cosine (radians) |
| `mathf.tan(x)` | `(float) → float` | Tangent (radians) |
| `mathf.abs(x)` | `(float) → float` | Absolute value |
| `mathf.sqrt(x)` | `(float) → float` | Square root |
| `mathf.floor(x)` | `(float) → float` | Floor |
| `mathf.ceil(x)` | `(float) → float` | Ceiling |
| `mathf.round(x)` | `(float) → float` | Round to nearest integer |
| `mathf.clamp(val, min, max)` | `(float, float, float) → float` | Clamp between min and max |
| `mathf.lerp(a, b, t)` | `(float, float, float) → float` | Linear interpolation |
| `mathf.min(a, b)` | `(float, float) → float` | Minimum of two values |
| `mathf.max(a, b)` | `(float, float) → float` | Maximum of two values |
| `mathf.random(min, max)` | `(float, float) → float` | Random float in range |
| `mathf.pi` | `float` | π ≈ 3.14159 |

---

### Physics Library (physics)

All functions are under the `physics` global table. These operate on Unity's global physics system.

| Function | Signature | Description |
|---|---|---|
| `physics.SetGravity(x, y, z)` | `(float, float, float) → void` | Set the global gravity vector |
| `physics.GetGravity()` | `() → {x, y, z}` | Get the current global gravity as a table |
| `physics.Raycast(ox, oy, oz, dx, dy, dz, maxDist)` | `(7× float) → table` | Cast a ray from origin `(ox,oy,oz)` in direction `(dx,dy,dz)`. Returns a table with `hit` (bool). If `hit` is `true`, also contains `name`, `x`, `y`, `z` (hit point), `distance`, `normalX`, `normalY`, `normalZ`. |
| `physics.CheckSphere(x, y, z, radius)` | `(float, float, float, float) → bool` | Returns `true` if any collider overlaps a sphere at `(x,y,z)` with the given `radius` |

#### Raycast Result Table

When `physics.Raycast` hits something:

```lua
local result = physics.Raycast(0, 1, 0,  0, -1, 0,  100)
if result.hit then
    self:Log("Hit: " .. result.name)
    self:Log("At: " .. result.x .. ", " .. result.y .. ", " .. result.z)
    self:Log("Distance: " .. result.distance)
end
```

| Field | Type | Description |
|---|---|---|
| `hit` | `bool` | `true` if the ray hit something, `false` otherwise |
| `name` | `string` | Name of the hit GameObject |
| `x`, `y`, `z` | `float` | World-space hit point |
| `distance` | `float` | Distance from ray origin to hit point |
| `normalX`, `normalY`, `normalZ` | `float` | Surface normal at the hit point |

---

## Scripting Examples

### 1. Spinning Object

```lua
local speed = 90  -- degrees per second

function OnUpdate()
    self:Rotate(0, speed * deltaTime(), 0)
end
```

### 2. Keyboard-Controlled Movement

```lua
local moveSpeed = 5

function OnUpdate()
    local dt = deltaTime()

    if self:GetKey("W") then
        self:Move(0, 0, moveSpeed * dt)
    end
    if self:GetKey("S") then
        self:Move(0, 0, -moveSpeed * dt)
    end
    if self:GetKey("A") then
        self:Move(-moveSpeed * dt, 0, 0)
    end
    if self:GetKey("D") then
        self:Move(moveSpeed * dt, 0, 0)
    end
end
```

### 3. Interactive Toggle

```lua
local isOn = false

function OnInteract(userId)
    isOn = not isOn
    if isOn then
        self:SetColor(0, 1, 0, 1)  -- green
        self:Log("Turned ON by " .. userId)
    else
        self:SetColor(1, 0, 0, 1)  -- red
        self:Log("Turned OFF by " .. userId)
    end
end
```

### 4. Proximity Trigger

```lua
local count = 0

function OnTriggerEnter(otherName)
    count = count + 1
    self:Log(otherName .. " entered. Count: " .. count)
    self:SetColor(1, 1, 0, 1)  -- yellow when occupied
end

function OnTriggerExit(otherName)
    count = count - 1
    if count <= 0 then
        count = 0
        self:SetColor(1, 1, 1, 1)  -- white when empty
    end
    self:Log(otherName .. " exited. Count: " .. count)
end
```

### 5. Timed Sequence (Coroutine)

```lua
function OnStart()
    StartCoroutine(LightShow)
end

function LightShow()
    while true do
        self:SetColor(1, 0, 0, 1)     -- Red
        coroutine.yield(wait(1.0))

        self:SetColor(0, 1, 0, 1)     -- Green
        coroutine.yield(wait(1.0))

        self:SetColor(0, 0, 1, 1)     -- Blue
        coroutine.yield(wait(1.0))
    end
end
```

### 6. Bobbing / Hovering Object

```lua
local startY = 0
local amplitude = 0.5
local frequency = 2

function OnStart()
    local pos = self:GetPosition()
    startY = pos.y
end

function OnUpdate()
    local pos = self:GetPosition()
    local newY = startY + mathf.sin(time() * frequency) * amplitude
    self:SetPosition(pos.x, newY, pos.z)
end
```

### 7. Object Talking to Another Object

```lua
function OnInteract(userId)
    local door = self:FindObject("Door")
    if door then
        door:SetPosition(0, 10, 0)      -- open the door
        door:SetColor(0, 1, 0, 1)       -- turn it green
        self:Log("Door opened!")
    else
        self:Log("Door not found!")
    end
end
```

### 8. Collectible with Respawn

```lua
local respawnTime = 5
local originalPos = nil

function OnStart()
    originalPos = self:GetPosition()
end

function OnInteract(userId)
    self:Log("Collected by " .. userId)
    self:SetActive(false)
    StartCoroutine(Respawn)
end

function Respawn()
    coroutine.yield(wait(respawnTime))
    self:SetActive(true)
    self:SetPosition(originalPos.x, originalPos.y, originalPos.z)
    self:Log("Respawned!")
end
```

### 9. Smooth Rotation with Math

```lua
function OnUpdate()
    local angle = time() * 45  -- 45 degrees per second
    self:SetRotation(0, angle, mathf.sin(time()) * 15)
end
```

### 10. Click Counter

```lua
local clicks = 0

function OnInteract(userId)
    clicks = clicks + 1
    self:Log("Clicks: " .. clicks)

    local intensity = mathf.clamp(clicks / 10, 0, 1)
    self:SetColor(intensity, 1 - intensity, 0, 1)
end
```

### 11. VR Grabbable with Highlight (XR)

```lua
function OnHoverEnter(hand)
    self:SetColor(1, 1, 0, 1)   -- yellow highlight
    self:Log(hand .. " is pointing at me")
end

function OnHoverExit(hand)
    self:SetColor(1, 1, 1, 1)   -- reset
end

function OnGrab(hand)
    self:SetColor(0, 1, 0, 1)   -- green while held
    self:Log("Grabbed by " .. hand)
end

function OnRelease(hand)
    self:SetColor(1, 1, 1, 1)   -- reset
    self:Log("Released by " .. hand)
end

function OnActivate(hand)
    self:Log("Trigger pressed by " .. hand .. " while holding me!")
    self:SetColor(1, 0, 0, 1)   -- flash red
    StartCoroutine(function()
        coroutine.yield(wait(0.3))
        self:SetColor(0, 1, 0, 1)
    end)
end
```

### 12. Orbit Around a Point

```lua
local orbitSpeed = 45  -- degrees per second

function OnUpdate()
    -- Orbit around the world origin on the Y axis
    self:RotateAround(0, 0, 0, 0, 1, 0, orbitSpeed * deltaTime())
end
```

### 13. Physics-Based Jump

```lua
local jumpForce = 8
local isGrounded = false

function OnStart()
    self:SetDrag(0.5)
end

function OnUpdate()
    -- Check if grounded using a short raycast downward
    local pos = self:GetPosition()
    local ground = physics.Raycast(pos.x, pos.y, pos.z, 0, -1, 0, 1.1)
    isGrounded = ground.hit

    if isGrounded and self:GetKeyDown("Space") then
        self:AddImpulse(0, jumpForce, 0)
        self:Log("Jump!")
    end
end
```

### 14. Explosion Knockback

```lua
function OnInteract(userId)
    -- Push this object upward and forward
    self:AddImpulse(0, 10, 5)
    self:Log("Boom! Triggered by " .. userId)
end
```

### 15. Zero-Gravity Zone

```lua
local originalGravity = nil

function OnTriggerEnter(otherName)
    -- Save original gravity and set to zero
    originalGravity = physics.GetGravity()
    physics.SetGravity(0, 0, 0)
    self:Log(otherName .. " entered zero-G zone")
end

function OnTriggerExit(otherName)
    -- Restore gravity
    if originalGravity then
        physics.SetGravity(originalGravity.x, originalGravity.y, originalGravity.z)
        self:Log(otherName .. " left zero-G zone")
    end
end
```

### 16. Ground Detection with Raycast

```lua
function OnUpdate()
    local pos = self:GetPosition()
    local result = physics.Raycast(pos.x, pos.y, pos.z, 0, -1, 0, 50)

    if result.hit then
        self:Log("Standing on: " .. result.name .. " at distance " .. result.distance)
    else
        self:Log("Nothing below!")
    end
end
```

### 17. Velocity-Based Color

```lua
function OnUpdate()
    local vel = self:GetVelocity()
    local speed = mathf.sqrt(vel.x * vel.x + vel.y * vel.y + vel.z * vel.z)
    local intensity = mathf.clamp(speed / 10, 0, 1)
    self:SetColor(intensity, 1 - intensity, 0, 1)  -- green when slow, red when fast
end
```

---

## What You CAN Write

With the current `LuaEngine` API you can build:

| Category | Examples |
|---|---|
| **Movement & Animation** | Spinning objects, hovering platforms, patrol paths, WASD controllers, follow-camera targets |
| **Interactables** | Buttons, switches, levers, doors, collectibles, toggles |
| **Triggers & Zones** | Proximity detectors, checkpoints, spawn zones, kill zones |
| **Visual Effects** | Color cycling, blinking, fade-in/out (via coroutines), scale pulsing |
| **Timed Sequences** | Cutscene-like chains, spawn timers, cooldowns, respawn logic |
| **Multi-Object Systems** | A button that opens a door, a switch that controls lights, linked platforms |
| **Game Logic** | Score counters, click trackers, timers, state machines (via Lua variables) |
| **Input-Driven Gameplay** | Keyboard/mouse controlled characters, aiming, directional movement |
| **Math-Driven Effects** | Sine/cosine wave motion, lerp-based smooth transitions, clamped values |
| **VR / XR Interactions** | Grabbable objects, hover highlights, trigger-activated tools, haptic feedback, two-handed interactions |
| **XR Grab Control** | Lock/unlock grabbing at runtime (`SetGrabbable`), haptic impulses on interact (`TriggerHaptic`) |
| **Audio** | Play/stop/toggle background music or SFX, volume/pitch control, looping audio |
| **Lighting** | Dynamic light color changes, flicker effects, on/off toggling, intensity pulsing |
| **Particle Effects** | Trigger bursts, looping VFX, grouped particle systems, stop-and-clear on event |
| **Hierarchy Traversal** | Control child objects from a parent script, propagate state to all children |
| **Text Display** | Update TMP labels in-world (score, status, name tags) |
| **Physics & Rigidbody** | Force-driven movement, jumping, knockback, explosions, velocity control, kinematic toggling |
| **Raycasting & Detection** | Ground checks, line-of-sight, distance sensing, sphere overlap queries |
| **Gravity Control** | Zero-gravity zones, modified gravity for gameplay effects |

---

## Limitations

| Limitation | Reason |
|---|---|
| **No `require` / `os` / `io`** | Sandboxed — forbidden calls are validated at export time |
| **Rigidbody must be added manually** | Physics functions (`AddForce`, `SetVelocity`, etc.) require a `Rigidbody` component on the GameObject |
| **XR requires XR Interaction Toolkit** | XR callbacks and `SetGrabbable`/`TriggerHaptic` only work when `com.unity.xr.interaction.toolkit` is installed |
| **AudioSource must be added manually** | Audio functions require an `AudioSource` component on the GameObject |
| **Light must be added manually** | Light functions require a `Light` component on the GameObject |
| **No UI API** | Cannot create or modify Canvas/UI elements from Lua |
| **No Instantiate / Spawn** | Cannot create new GameObjects at runtime |
| **No direct C# type access** | MoonSharp is configured for sandboxed mode only |
| **`FindObject` by name only** | Objects must have unique names; name collisions use the last-registered instance |
| **`OnUpdate` has no deltaTime arg** | Must call `deltaTime()` globally instead |
| **No network / multiplayer API** | Cannot send messages to other players or call network methods |
| **Single `TextAsset` per object** | Each `ScenarioBridge` holds one Lua script |
| **`SetColor` takes 4 args (r,g,b,a)** | Alpha is required — use `1` to keep full opacity: `self:SetColor(1, 0, 0, 1)` |

---

## Setup Checklist

1. Add a **ScenarioBridge** component to your prefab.
2. Assign a `.lua.txt` TextAsset to the `luaScript` field.
3. The **LuaEngine** component is auto-added (via `[RequireComponent]`).
4. Add a **Collider** (set to **Is Trigger**) if using `OnTriggerEnter`/`OnTriggerExit`.
5. *(Optional)* Install **XR Interaction Toolkit** for VR/AR support — `LuaEngine` auto-adds `XRSimpleInteractable` and a `BoxCollider` if missing.
6. Press Play — check the Console for `[LuaEngine] ✓ Script loaded on <Name>` (and `✓ XR enabled on <Name>` if XR is active).
