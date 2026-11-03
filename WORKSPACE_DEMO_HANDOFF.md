# Workspace and Demo handoff

Register the two singleton services in the shared composition root:

```csharp
services.AddSingleton<WorkspacePreferencesService>();
services.AddSingleton<UnsavedChangesService>();
```

Both are in Plinth.App.Services. MainViewModel and DemoListViewModel must receive the same confirmation service. Without the preferences registration, defaults work but persistence is disabled.
