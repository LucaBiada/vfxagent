// Keeps exposed VisualEffect values set from code. A VisualEffect DROPS values set at runtime (SetFloat, SetVector4, ...)
// when the component is disabled and enabled again (seen 2026-11-17: a dissolve that toggled effect.enabled silently
// reverted tuned values to the graph defaults). Set the values through this component instead: it re-applies every
// held value each LateUpdate, so a toggle costs at most one frame of default values.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.VFX;

[RequireComponent(typeof(VisualEffect))]
[AddComponentMenu("Visual Effects/VFX Property Hold")]
public class VfxPropertyHold : MonoBehaviour
{
    public enum Kind { Float, Int, Bool, Vector2, Vector3, Vector4, Texture }

    [Serializable]
    public class Entry
    {
        public string name;
        public Kind kind;
        public Vector4 value;   // Float/Int/Bool use x; Vector2/3 use xy/xyz; colours go in Vector4 (VFX colours are float4)
        public Texture texture;
    }

    public List<Entry> entries = new List<Entry>();
    VisualEffect m_Vfx;

    public void SetFloat(string name, float v) => Hold(name, Kind.Float, new Vector4(v, 0, 0, 0));
    public void SetInt(string name, int v) => Hold(name, Kind.Int, new Vector4(v, 0, 0, 0));
    public void SetBool(string name, bool v) => Hold(name, Kind.Bool, new Vector4(v ? 1 : 0, 0, 0, 0));
    public void SetVector2(string name, Vector2 v) => Hold(name, Kind.Vector2, v);
    public void SetVector3(string name, Vector3 v) => Hold(name, Kind.Vector3, v);
    public void SetVector4(string name, Vector4 v) => Hold(name, Kind.Vector4, v);
    public void SetColor(string name, Color c) => Hold(name, Kind.Vector4, c);
    public void SetTexture(string name, Texture t) { var e = Hold(name, Kind.Texture, Vector4.zero); e.texture = t; ApplyEntry(e); }

    /// Stops holding a value and gives the property back to the graph default.
    public void Release(string name)
    {
        entries.RemoveAll(e => e.name == name);
        if (Vfx != null) Vfx.ResetOverride(name);
    }

    VisualEffect Vfx => m_Vfx != null ? m_Vfx : m_Vfx = GetComponent<VisualEffect>();

    Entry Hold(string name, Kind kind, Vector4 v)
    {
        var e = entries.Find(x => x.name == name);
        if (e == null) entries.Add(e = new Entry { name = name });
        e.kind = kind; e.value = v;
        ApplyEntry(e);
        return e;
    }

    void OnEnable() => Apply();
    void LateUpdate() => Apply();

    /// Re-applies every held value (LateUpdate does this; call it right after re-enabling the VisualEffect to skip the frame).
    public void Apply() { foreach (var e in entries) ApplyEntry(e); }

    void ApplyEntry(Entry e)
    {
        var v = Vfx;
        if (v == null || string.IsNullOrEmpty(e.name)) return;
        switch (e.kind)
        {
            case Kind.Float: if (v.HasFloat(e.name)) v.SetFloat(e.name, e.value.x); break;
            case Kind.Int: if (v.HasInt(e.name)) v.SetInt(e.name, Mathf.RoundToInt(e.value.x)); break;
            case Kind.Bool: if (v.HasBool(e.name)) v.SetBool(e.name, e.value.x != 0); break;
            case Kind.Vector2: if (v.HasVector2(e.name)) v.SetVector2(e.name, e.value); break;
            case Kind.Vector3: if (v.HasVector3(e.name)) v.SetVector3(e.name, e.value); break;
            case Kind.Vector4: if (v.HasVector4(e.name)) v.SetVector4(e.name, e.value); break;
            case Kind.Texture: if (v.HasTexture(e.name)) v.SetTexture(e.name, e.texture); break;
        }
    }
}
