// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Annotations;
using Stride.Core.Presentation.ViewModels;

namespace Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;

/// <summary>
/// Declares an <see cref="EntityHierarchyEditorToolViewModel"/> that editors of an <see cref="EditorType"/> create, so a package
/// can add a tool to the scene or prefab editor. Its constructor takes the view model service provider, the editor game
/// controller and the editor view model.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
[Stride.Core.Reflection.AssemblyScan]
public sealed class EntityHierarchyEditorToolAttribute : Attribute
{
    public EntityHierarchyEditorToolAttribute(Type editorType)
    {
        EditorType = editorType;
    }

    public Type EditorType { get; }

    /// <summary>
    /// Sorts the tools in the viewport toolbar (ties by type name).
    /// </summary>
    public int Order { get; set; }
}

/// <summary>
/// A tool of an entity hierarchy editor, declared by <see cref="EntityHierarchyEditorToolAttribute"/>: a toggle in the viewport
/// toolbar and, while it is active, a panel beside the viewport. The panel is the data template of the tool's type, which the
/// package's view assembly adds to the application resources. One tool is active at a time.
/// </summary>
public abstract class EntityHierarchyEditorToolViewModel : DispatcherViewModel
{
    private bool isActive;

    protected EntityHierarchyEditorToolViewModel([NotNull] IViewModelServiceProvider serviceProvider)
        : base(serviceProvider)
    {
    }

    /// <summary>
    /// The label of the toolbar toggle.
    /// </summary>
    public abstract string DisplayName { get; }

    public virtual string ToolTip => null;

    /// <summary>
    /// Key of an image in the application resources (e.g. a DrawingImage added by the package's view assembly) shown on the
    /// toolbar toggle instead of <see cref="DisplayName"/>; null or not found: the name is shown.
    /// </summary>
    public virtual string IconResourceKey => null;

    /// <summary>
    /// Whether the tool can be activated (raise a property change when it changes).
    /// </summary>
    public virtual bool IsAvailable => true;

    public bool IsActive { get => isActive; set => SetValue(ref isActive, value, () => OnIsActiveChanged()); }

    protected virtual void OnIsActiveChanged()
    {
    }
}
