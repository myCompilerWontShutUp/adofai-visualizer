using System;
using System.Reflection;
using UnityEngine;

namespace AdofaiVisualizerSync
{
    /// <summary>
    /// Hides JipperResourcePack's key viewer while the game is not the focused window and brings it back when it is.
    /// That mod has no option of its own for this (its settings and its labels only cover style, keys, position, size,
    /// colours, rain and KPS), so the user allowed this one write outside our own state.
    ///
    /// It touches nothing else of that mod: only the key viewer's own GameObject is switched, the mod's files are never
    /// read or written, and if the mod is missing — or a later version moves the field — this quietly does nothing.
    /// </summary>
    internal sealed class KeyViewerFocus
    {
        private const string TypeName = "JipperResourcePack.KeyViewerContents.KeyViewer";
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;

        private FieldInfo? _instance;
        private FieldInfo? _viewerObject;
        private bool _given;
        private bool? _shown;
        /// <summary>The focus a lookup last found no key viewer for, so it is not looked for again until the focus changes.</summary>
        private bool? _missed;

        /// <summary>
        /// Follows the window's focus. Called every frame, it does nothing at all until the focus actually changes, so
        /// the reflection below runs a handful of times a session rather than sixty times a second.
        /// </summary>
        public void Follow()
        {
            var focused = Application.isFocused;
            if (_given || _shown == focused || _missed == focused) return;
            if (Show(focused)) { _shown = focused; _missed = null; }
            // Without the key viewer mod this would search every loaded assembly every frame.
            else _missed = focused;
        }

        /// <summary>Leaves the key viewer visible again: this mod being switched off must not hide someone else's.</summary>
        public void Release()
        {
            if (_shown == false) Show(true);
            _shown = null;
            _missed = null;
        }

        private bool Show(bool shown)
        {
            try
            {
                if (Viewer() is not { } viewer) return false;
                if (viewer.activeSelf != shown) viewer.SetActive(shown);
                return true;
            }
            catch (Exception)
            {
                // Never let another mod's shape stop the game: one failure is enough to leave it alone for good.
                _given = true;
                return false;
            }
        }

        private GameObject? Viewer()
        {
            if (_instance is null || _viewerObject is null)
            {
                // Looked up on a focus change, not once at startup: the key viewer may well load after this mod does.
                var type = FindType();
                _instance = type?.GetField("Instance", Any | BindingFlags.Static);
                _viewerObject = type?.GetField("KeyViewerObject", Any | BindingFlags.Instance);
                if (_instance is null || _viewerObject is null) return null;
            }
            var viewer = _instance.GetValue(null);
            return viewer is null ? null : _viewerObject.GetValue(viewer) as GameObject;
        }

        private static Type? FindType()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetType(TypeName, throwOnError: false) is { } found) return found;
            }
            return null;
        }
    }
}
