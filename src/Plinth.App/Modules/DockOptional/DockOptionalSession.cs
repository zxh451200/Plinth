using System;
using System.Collections.Generic;

namespace Plinth.App.Modules.DockOptional;

/// <summary>UI-thread resource ownership boundary; no Dock engine or registration.</summary>
public sealed class DockOptionalSession : IDisposable
{
    private readonly List<IDisposable> _resources = [];
    private bool _disposed;

    public T Own<T>(T resource) where T : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(resource);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_resources.Exists(item => ReferenceEquals(item, resource)))
            _resources.Add(resource);
        return resource;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception> failures = [];
        for (var index = _resources.Count - 1; index >= 0; index--)
        {
            try { _resources[index].Dispose(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { failures.Add(ex); }
        }
        _resources.Clear();
        if (failures.Count > 0) throw new AggregateException(failures);
    }
}
