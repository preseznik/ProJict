using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services;
using ProJict.Core.Tools;

namespace ProJict.App.ViewModels;

/// <summary>
/// A ComfyUI tool in the Tools window: its endpoints (name and address, with a live check), whether agents may run its
/// workflows, and the workflow library (added from files exported with Workflow › Export (API)).
/// </summary>
public sealed partial class ToolEditorViewModel
{
    public bool IsComfy
    {
        get => tool.Access == ToolAccess.ComfyUI;
        set
        {
            if (!value || tool.Access == ToolAccess.ComfyUI) return;
            tool.Access = ToolAccess.ComfyUI;
            if (tool.Endpoints is not { Count: > 0 }) tool.Endpoints = [new ComfyEndpoint { Name = "This computer", Address = "http://127.0.0.1:8188" }];
            Changed(nameof(IsApi), nameof(IsBrowser), nameof(IsComfy), nameof(IsProgram));
            RefreshComfy();
        }
    }

    private ObservableCollection<ComfyEndpointRow>? _endpoints;
    public ObservableCollection<ComfyEndpointRow> Endpoints => _endpoints ??= [.. (tool.Endpoints ?? []).Select(e => new ComfyEndpointRow(e, this))];

    private ObservableCollection<WorkflowRow>? _workflows;
    public ObservableCollection<WorkflowRow> Workflows => _workflows ??= [.. main.Workflows.Of(tool.Name).Select(w => new WorkflowRow(w, this))];
    public bool HasWorkflows => Workflows.Count > 0;
    public string? WorkflowProblem { get; private set; }

    /// <summary>Agents may run its workflows (they can always read them).</summary>
    public bool AllowRuns
    {
        get => !tool.RunsOff;
        set
        {
            if (value == !tool.RunsOff) return;
            tool.RunsOff = !value;
            Changed(nameof(AllowRuns));
        }
    }

    private void RefreshComfy()
    {
        _endpoints = null;
        _workflows = null;
        foreach (var name in new[] { nameof(Endpoints), nameof(Workflows), nameof(HasWorkflows), nameof(Subtitle) }) OnPropertyChanged(name);
    }

    internal ComfyClient Comfy => main.AgentTools.Comfy;
    internal void SaveTools() => Service.Save();
    internal void SaveWorkflow(ComfyWorkflow workflow) => main.Workflows.Save(workflow);

    [RelayCommand]
    private void AddEndpoint()
    {
        var name = "Endpoint";
        for (var n = 2; tool.Endpoints?.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) == true; n++) name = $"Endpoint {n}";
        var endpoint = new ComfyEndpoint { Name = name, Address = "http://" };
        (tool.Endpoints ??= []).Add(endpoint);
        Service.Save();
        Endpoints.Add(new ComfyEndpointRow(endpoint, this));
    }

    internal void RemoveEndpoint(ComfyEndpointRow row)
    {
        tool.Endpoints?.Remove(row.Model);
        Endpoints.Remove(row);
        Service.Save();
    }

    /// <summary>Adds a workflow from a file exported from ComfyUI (Workflow › Export (API)); says why not when it can't.</summary>
    public void AddWorkflowFile(string path)
    {
        try
        {
            var workflow = main.Workflows.Add(tool.Name, Path.GetFileNameWithoutExtension(path), File.ReadAllText(path), main.State.UserName);
            Workflows.Add(new WorkflowRow(workflow, this));
            WorkflowProblem = null;
            main.State.StatusText = $"Added the workflow “{workflow.Name}” to {tool.Name}: {string.Join(", ", workflow.Inputs.Select(i => i.Key))}.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            WorkflowProblem = ex.Message;
        }
        foreach (var name in new[] { nameof(WorkflowProblem), nameof(HasWorkflows), nameof(Subtitle) }) OnPropertyChanged(name);
    }

    internal void RemoveWorkflow(WorkflowRow row)
    {
        main.Workflows.Remove(row.Model);
        Workflows.Remove(row);
        foreach (var name in new[] { nameof(HasWorkflows), nameof(Subtitle) }) OnPropertyChanged(name);
        main.State.StatusText = $"Removed the workflow “{row.Model.Name}”.";
    }
}

/// <summary>A ComfyUI endpoint: its name and address, edited in place, and whether it answers.</summary>
public sealed partial class ComfyEndpointRow(ComfyEndpoint endpoint, ToolEditorViewModel owner) : ObservableObject
{
    public ComfyEndpoint Model => endpoint;

    public string Name
    {
        get => endpoint.Name;
        set
        {
            value = (value ?? "").Trim();
            if (value.Length == 0 || value == endpoint.Name) return;
            endpoint.Name = value;
            owner.SaveTools();
            OnPropertyChanged();
        }
    }

    public string Address
    {
        get => endpoint.Address;
        set
        {
            // Cut back to the server's own address (a pasted …/prompt goes), shown as kept.
            var address = ComfyEndpoint.ServerAddress(value);
            if (address != endpoint.Address)
            {
                endpoint.Address = address;
                owner.SaveTools();
                Status = "";
            }
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private string _status = "";

    /// <summary>Asks the server how it's doing.</summary>
    [RelayCommand]
    private async Task Check()
    {
        Status = "Checking…";
        Status = (await owner.Comfy.CheckAsync(endpoint)).Describe();
    }

    [RelayCommand] private void Remove() => owner.RemoveEndpoint(this);
}

/// <summary>A workflow in a ComfyUI tool's library: its name and what it makes, edited in place, and its inputs.</summary>
public sealed partial class WorkflowRow(ComfyWorkflow workflow, ToolEditorViewModel owner) : ObservableObject
{
    public ComfyWorkflow Model => workflow;

    public string Name
    {
        get => workflow.Name;
        set
        {
            value = (value ?? "").Trim();
            if (value.Length == 0 || value == workflow.Name) return;
            workflow.Name = value;
            owner.SaveWorkflow(workflow);
            OnPropertyChanged();
        }
    }

    public string Purpose
    {
        get => workflow.Purpose;
        set
        {
            value = (value ?? "").Trim();
            if (value == workflow.Purpose) return;
            workflow.Purpose = value;
            owner.SaveWorkflow(workflow);
            OnPropertyChanged();
        }
    }

    /// <summary>"Inputs: prompt, negative, seed, width, height".</summary>
    public string InputsText => workflow.Inputs.Count > 0 ? "Inputs: " + string.Join(", ", workflow.Inputs.Select(i => i.Key)) : "No inputs found: agents run it as it is.";

    [RelayCommand] private void Remove() => owner.RemoveWorkflow(this);
}
