using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Collections.Pooled;
using Avalonia.Media;
using Avalonia.VisualTree;

// Special license applies <see href="https://raw.githubusercontent.com/AvaloniaUI/Avalonia/master/src/Avalonia.Base/Rendering/Composition/License.md">License.md</see>

namespace Avalonia.Rendering.Composition;

/// <summary>
/// A renderer that utilizes <see cref="Avalonia.Rendering.Composition.Compositor"/> to render the visual tree 
/// </summary>
public class CompositingRenderer : IRendererWithCompositor
{
    private readonly IRenderRoot _root;
    private readonly Compositor _compositor;
    CompositionDrawingContext _recorder = new();
    DrawingContext _recordingContext;
    private HashSet<Visual> _dirty = new();
    private HashSet<Visual> _recalculateChildren = new();
    private bool _queuedUpdate;
    private Action _update;
    private bool _updating;

    internal CompositionTarget CompositionTarget;
    
    /// <summary>
    /// Asks the renderer to only draw frames on the render thread. Makes Paint to wait until frame is rendered.
    /// </summary>
    public bool RenderOnlyOnRenderThread { get; set; } = true;

    /// <inheritdoc/>
    public RendererDiagnostics Diagnostics { get; }

    public CompositingRenderer(IRenderRoot root, Compositor compositor, Func<IEnumerable<object>> surfaces)
    {
        _root = root;
        _compositor = compositor;
        _recordingContext = new DrawingContext(_recorder);
        CompositionTarget = compositor.CreateCompositionTarget(surfaces);
        CompositionTarget.Root = ((Visual)root).AttachToCompositor(compositor);
        _update = Update;
        Diagnostics = new RendererDiagnostics();
        Diagnostics.PropertyChanged += OnDiagnosticsPropertyChanged;
    }

    private void OnDiagnosticsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RendererDiagnostics.DebugOverlays):
                CompositionTarget.DebugOverlays = Diagnostics.DebugOverlays;
                break;
            case nameof(RendererDiagnostics.LastLayoutPassTiming):
                CompositionTarget.LastLayoutPassTiming = Diagnostics.LastLayoutPassTiming;
                break;
        }
    }

    /// <inheritdoc/>
    public event EventHandler<SceneInvalidatedEventArgs>? SceneInvalidated;

    void QueueUpdate()
    {
        if(_queuedUpdate)
            return;
        _queuedUpdate = true;
        _compositor.RequestCompositionUpdate(_update);
    }
    
    /// <inheritdoc/>
    public void AddDirty(Visual visual)
    {
        if (_updating)
            throw new InvalidOperationException("Visual was invalidated during the render pass");
        _dirty.Add((Visual)visual);
        QueueUpdate();
    }

    /// <inheritdoc/>
    public IEnumerable<Visual> HitTest(Point p, Visual? root, Func<Visual, bool>? filter)
    {
        CompositionVisual? rootVisual = null;
        if (root != null)
        {
            if (root.CompositionVisual == null)
                yield break;
            rootVisual = root.CompositionVisual;
        }
        
        Func<CompositionVisual, bool>? f = null;
        if (filter != null)
            f = v =>
            {
                if (v is CompositionDrawListVisual dlv)
                    return filter(dlv.Visual);
                return true;
            };

        var res = CompositionTarget.TryHitTest(p, rootVisual, f);
        if(res == null)
            yield break;
        foreach(var v in res)
        {
            if (v is CompositionDrawListVisual dv)
            {
                if (filter == null || filter(dv.Visual))
                    yield return dv.Visual;
            }
        }
    }

    /// <inheritdoc/>
    public Visual? HitTestFirst(Point p, Visual root, Func<Visual, bool>? filter)
    {
        // TODO: Optimize
        return HitTest(p, root, filter).FirstOrDefault();
    }

    /// <inheritdoc/>
    public void RecalculateChildren(Visual visual)
    {
        if (_updating)
            throw new InvalidOperationException("Visual was invalidated during the render pass");
        _recalculateChildren.Add((Visual)visual);
        QueueUpdate();
    }

    private static void SyncChildren(Visual v)
    {
        //TODO: Optimize by moving that logic to Visual itself
        if(v.CompositionVisual == null)
            return;
        var compositionChildren = v.CompositionVisual.Children;
        var visualChildren = (AvaloniaList<Visual>)v.GetVisualChildren();
        
        PooledList<(Visual visual, int index)>? sortedChildren = null;
        if (v.HasNonUniformZIndexChildren && visualChildren.Count > 1)
        {
            sortedChildren = new (visualChildren.Count);
            for (var c = 0; c < visualChildren.Count; c++) 
                sortedChildren.Add((visualChildren[c], c));
            
            // Regular Array.Sort is unstable, we need to provide indices as well to avoid reshuffling elements.
            sortedChildren.Sort(static (lhs, rhs) =>
            {
                var result = lhs.visual.ZIndex.CompareTo(rhs.visual.ZIndex);
                return result == 0 ? lhs.index.CompareTo(rhs.index) : result;
            });
        }
        
        var childVisual = v.ChildCompositionVisual;
        
        // Check if the current visual somehow got migrated to another compositor
        if (childVisual != null && childVisual.Compositor != v.CompositionVisual.Compositor)
            childVisual = null;
        
        var expectedCount = visualChildren.Count;
        if (childVisual != null)
            expectedCount++;
        
        if (compositionChildren.Count == expectedCount)
        {
            bool mismatch = false;
            if (sortedChildren != null)
                for (var c = 0; c < visualChildren.Count; c++)
                {
                    if (!ReferenceEquals(compositionChildren[c], ((Visual)sortedChildren[c].visual).CompositionVisual))
                    {
                        mismatch = true;
                        break;
                    }
                }
            else
                for (var c = 0; c < visualChildren.Count; c++)
                    if (!ReferenceEquals(compositionChildren[c], ((Visual)visualChildren[c]).CompositionVisual))
                    {
                        mismatch = true;
                        break;
                    }

            if (childVisual != null &&
                !ReferenceEquals(compositionChildren[compositionChildren.Count - 1], childVisual))
                mismatch = true;

            if (!mismatch)
            {
                sortedChildren?.Dispose();
                return;
            }
        }
        
        compositionChildren.Clear();
        if (sortedChildren != null)
        {
            foreach (var ch in sortedChildren)
            {
                var compositionChild = ((Visual)ch.visual).CompositionVisual;
                if (compositionChild != null)
                    compositionChildren.Add(compositionChild);
            }
            sortedChildren.Dispose();
        }
        else
            foreach (var ch in v.GetVisualChildren())
            {
                var compositionChild = ((Visual)ch).CompositionVisual;
                if (compositionChild != null)
                    compositionChildren.Add(compositionChild);
            }

        if (childVisual != null)
            compositionChildren.Add(childVisual);
    }

    private void UpdateCore()
    {
        _queuedUpdate = false;
        foreach (var visual in _dirty)
        {
            var comp = visual.CompositionVisual;
            if(comp == null)
                continue;
            
            // TODO: Optimize all of that by moving to the Visual itself, so we won't have to recalculate every time
            comp.Offset = new Vector3((float)visual.Bounds.Left, (float)visual.Bounds.Top, 0);
            comp.Size = new Vector2((float)visual.Bounds.Width, (float)visual.Bounds.Height);
            comp.Visible = visual.IsVisible;
            comp.Opacity = (float)visual.Opacity;
            comp.ClipToBounds = visual.ClipToBounds;
            comp.Clip = visual.Clip?.PlatformImpl;
            comp.OpacityMask = visual.OpacityMask;
            
            var renderTransform = Matrix.Identity;

            if (visual.HasMirrorTransform) 
                renderTransform = new Matrix(-1.0, 0.0, 0.0, 1.0, visual.Bounds.Width, 0);

            if (visual.RenderTransform != null)
            {
                var origin = visual.RenderTransformOrigin.ToPixels(new Size(visual.Bounds.Width, visual.Bounds.Height));
                var offset = Matrix.CreateTranslation(origin);
                renderTransform *= (-offset) * visual.RenderTransform.Value * (offset);
            }



            comp.TransformMatrix = MatrixUtils.ToMatrix4x4(renderTransform);

            _recorder.BeginUpdate(comp.DrawList);
            visual.Render(_recordingContext);
            comp.DrawList = _recorder.EndUpdate();

            SyncChildren(visual);
        }
        foreach(var v in _recalculateChildren)
            if (!_dirty.Contains(v))
                SyncChildren(v);
        _dirty.Clear();
        _recalculateChildren.Clear();
        CompositionTarget.Size = _root.ClientSize;
        CompositionTarget.Scaling = _root.RenderScaling;
        TriggerSceneInvalidatedOnBatchCompletion(_compositor.RequestCommitAsync());
    }

    private async void TriggerSceneInvalidatedOnBatchCompletion(Task batchCompletion)
    {
        await batchCompletion;
        SceneInvalidated?.Invoke(this, new SceneInvalidatedEventArgs(_root, new Rect(_root.ClientSize)));
    }
    
    private void Update()
    {
        if(_updating)
            return;
        _updating = true;
        try
        {
            UpdateCore();
        }
        finally
        {
            _updating = false;
        }
    }
    
    public void Resized(Size size)
    {
    }

    public void Paint(Rect rect)
    {
        QueueUpdate();
        CompositionTarget.RequestRedraw();
        if(RenderOnlyOnRenderThread && Compositor.Loop.RunsInBackground)
            Compositor.Commit().Wait();
        else
            CompositionTarget.ImmediateUIThreadRender();
    }

    public void Start() => CompositionTarget.IsEnabled = true;

    public void Stop()
    {
        CompositionTarget.IsEnabled = false;
    }

    public ValueTask<object?> TryGetRenderInterfaceFeature(Type featureType) => Compositor.TryGetRenderInterfaceFeature(featureType);

    public void Dispose()
    {
        Stop();
        CompositionTarget.Dispose();
        
        // Wait for the composition batch to be applied and rendered to guarantee that
        // render target is not used anymore and can be safely disposed
        if (Compositor.Loop.RunsInBackground)
            _compositor.Commit().Wait();
    }

    /// <summary>
    /// The associated <see cref="Avalonia.Rendering.Composition.Compositor"/> object
    /// </summary>
    public Compositor Compositor => _compositor;
}
