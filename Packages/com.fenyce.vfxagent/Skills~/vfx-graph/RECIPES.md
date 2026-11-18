# VFX Graph recipes (tested in a real project, 2026-11-17)

Techniques that worked while building a 4M-particle GPU dust field, touch ripples, a restyled ember graph and
particles that form skinned bodies. Each Custom HLSL block lives in its own `.hlsl` file (one attribute function per
file, no apostrophes anywhere in the file) and is added with `hlsl <ctx> "<block>" file=<path> function=<name>`.
Link every input explicitly afterwards (`link2 P:<param> "B:<ctx>/<block>/_<input>"`) and check the `<-P:<param>` list
in the `hlsl` log line. Custom HLSL inputs are the function's parameters with a `_` prefix (`_Radius`).

## Per-particle randomness in a script-added block
`seed` and `particleId` are not stored for a script-added block (reading them fails the GPU compile with
"invalid subscript"). Hash the random lifetime instead: use a `Set Lifetime Random` with **Min != Max**.

```hlsl
float MyRand(inout uint n)   // PCG hash, 0..1
{
    n = n * 747796405u + 2891336453u;
    uint w = ((n >> ((n >> 28u) + 4u)) ^ n) * 277803737u;
    return ((w >> 22u) ^ w) / 4294967295.0;
}
// in the function: uint n = asuint(attributes.lifetime) ^ 1597334677u;   (a different salt per block = independent streams)
```
The same hash gives the same numbers every frame, so a particle keeps "its" frequency, phase, target or offset without
storing anything. Name the helper per file (`DustDriftRand`, `DustTwinkleRand`): every block includes its whole file.

## Start fully formed (no fade-in of the first particles)
Init HLSL: `attributes.age = attributes.lifetime * MyRand(n) * StartAged;` with a `StartAged` float param. From C#:
for the first 3 frames set `StartAged = 1` and `Rate = Count / (3 * Time.deltaTime)`; then `StartAged = 0` and the steady
rate `Count / mean lifetime` (plus a small fill boost from `GetParticleSystemInfo(name).aliveCount` when below Count).

## Individual drift (each particle on its own wobble)
The Turbulence block is a shared field: neighbours move together and the whole cloud reads as one thing. For the
"every speck alone" feel (Shuriken noise in Low quality), give each particle a target velocity from two sines per axis,
rates and phases from the hash, and ease toward it:
```hlsl
float t = attributes.age * Frequency * 6.2831853;
float3 f1 = 0.6 + float3(MyRand(n), MyRand(n), MyRand(n)) * 0.8;   // rates
float3 f2 = 1.3 + float3(MyRand(n), MyRand(n), MyRand(n)) * 1.4;
float3 p1 = float3(MyRand(n), MyRand(n), MyRand(n)) * 6.2831853;   // phases
float3 p2 = float3(MyRand(n), MyRand(n), MyRand(n)) * 6.2831853;
float3 target = (sin(t * f1 + p1) + 0.5 * sin(t * f2 + p2)) * (Strength / 1.5);
attributes.velocity += (target - attributes.velocity) * saturate(Drag * DeltaTime);
```

## Galaxy-like clumps
`offset = randomDirection * Radius * r * r` (r = MyRand, the extra `* r` makes a denser core and a sparse edge).
Squash only the clump CENTRE into a flattened volume, never the offset: squashing the offset gives flat discs with a
bright line through them.

## Emergent filaments (cosmic web, neurons) from motion, not placement
Two slowly drifting 3D value-noise fields A and B; where both are 0 is a set of curves. Move every particle down the
gradient of `A*A + B*B` (eased off within `Width` of a curve) and let it flow along `cross(gradA, gradB)` on the curve.
Evenly spread particles gather into filaments that pile up where they meet (the "nodes"), and the web reshapes as the
fields drift with `Time * Evolve`. Central differences: 13 noise evaluations per particle per frame, fine at 1M on an
RTX 5090.
```hlsl
float a = A(p), b = B(p);                     // A(p) = Noise(p * F + float3(0, T, 0)), B = another offset/scale
float3 ga = grad(A, p), gb = grad(B, p);      // (A(p + e.x) - A(p - e.x)) / 2e per axis, e = 0.08 / F
float d = sqrt(a * a + b * b);
float3 g = a * ga + b * gb;                   // gradient of (A*A + B*B) / 2
float3 v = -normalize(g) * Strength * saturate(d / Width);
v += normalize(cross(ga, gb)) * Flow * (1 - saturate(d / Width));
attributes.position += v * DeltaTime;         // position, so velocity-based blocks stay on top
```

## Ripples of light from touches
C# keeps the last N touches (8 worked) as params: `Wave i Position` (vector3) and `Wave i Time` (float, in the same clock
as a `Time` param). Per particle and wave: `r = distance`, `since = Time - t0 - r / Speed`; once `since >= 0`,
`glow += exp(-since / Fade) * exp(-r / Range)`. Then `color = base * lerp(BaseLevel, 1, saturate(glow))`.
(`param vector4` exists since 0.1.3, so position + time can also travel as one float4.)

## Floor collision
The LAST Update block: within `Margin` above the floor push up softly (`v.y += Repel * (1 - h / Margin) * dt`); below
the floor clamp `p.y = FloorY` and reflect `v.y = -v.y * Bounce` when moving down.

## Per-particle twinkle
`hz = lerp(SpeedMin, SpeedMax, MyRand(n))`, `phase = MyRand(n) * 2pi`,
`alpha = 1 - Amount + Amount * pow(0.5 + 0.5 * sin(age * hz * 2pi + phase), Sharpness)` (1 = soft breathing, 6 = flash).
Writing `attributes.alpha` from Update needs alpha STORED: `addblock Init SetAttribute attribute=alpha` first.

## Skinned points baked to a texture (several bodies in one effect)
The built-in skinned-mesh sampling takes one renderer per graph. For several bodies in ONE effect:
1. C#: `smr.vertexBufferTarget |= GraphicsBuffer.Target.Raw`; each frame `smr.GetVertexBuffer()` (null until the
   renderer has skinned once; Dispose it after use).
2. A small compute shader copies N chosen vertices (an index buffer picked at random once) into a float RenderTexture:
   row m = body m, texel k = point k, xyz = world position (through the root bone matrix), w = a per-body level (form 0..1).
3. The graph gets it as `param texture2d "Points"` (a RenderTexture is accepted by `VisualEffect.SetTexture`); Custom HLSL
   input `VFXSampler2D Points`, read with `Points.t.Load(int3(k, m, 0))`.
4. Each particle picks body m and point k from its lifetime hash (so it keeps its place) and lerps onto it with
   `saturate(f * f * Pull * dt)`, `velocity *= 1 - f`; when the level drops it is let go.

## Restyle a graph without touching its behaviour
`setctx Output mainTexture=asset:<texture>`; an Orient block's mode is a SETTING (`setting Output "Orient: Along Velocity"
mode=FaceCameraPlane`); `disable` the stretch (Scale.Y) blocks; `paramset "ColorGradient" value=gradient:0=r,g,b,a;0.1=...`
(time=r,g,b,a keys, HDR allowed). First check the scene VisualEffects for per-instance overrides (SerializedObject
`m_PropertySheet` entries with `m_Overridden`): an override hides the new graph default.

## Inward homing after Turbulence ("fly in and land on the skin")
Reverse a "spawn on the skin and fly off" effect. Init: `addblock Init SetAttribute attribute=targetPosition` (makes it
stored), then a Custom HLSL after the skinned position: `targetPosition = position`, move the start out along a hashed
direction. Update: KEEP the original Turbulence block and add a homing block AFTER it:
```hlsl
float t = saturate(attributes.age / max(attributes.lifetime, 0.001));
float k = pow(t, max(Power, 0.1));
attributes.position = lerp(attributes.position, attributes.targetPosition, k);
attributes.velocity *= 1.0 - k;
```
Free turbulence early, an exact landing at death. A purely kinematic path (lerp + swirl, Turbulence off) reads as dead.
The target is fixed at birth: fine for short lives on a moving body.

## Shader and VFX sharing one procedural edge
Put the same noise function in the material HLSL (as globals with default 0, so other scenes are unchanged) and in the
VFX Custom HLSL (as params), and feed both the same values and the same time (`Time.time`) from C#. The particles then
sit exactly on the dissolving edge.

## Holding runtime values
A VisualEffect drops values set with `SetFloat` / `SetVector4` / ... when the component is disabled and enabled again:
they silently revert to the graph defaults. Set them through `VfxPropertyHold` (Runtime assembly of this package), which
re-applies every held value each LateUpdate: `GetComponent<VfxPropertyHold>().SetFloat("Gather", 0.7f)`.

## Live look iteration
One graph with every behaviour behind parameters (a master `0..1` mixed in with Multiply/Lerp operators, so 0 = the
original look), the scene in Play, variants applied by eval on a tuner component, a seekable clock to replay the
moment. No graph edit and no compile during Play. Judge in Play only: in Edit mode `aliveCount` reads 0 and a manual
`Simulate()` + render shows nothing.

## Budget (RTX 5090)
4M alive specks of 2-7 mm: about 2 ms GPU in the Editor, CPU flat. In a headset a 2 mm speck is one pixel and its soft
halo never shows: fewer, larger (5-15 cm) halo specks read far better.
