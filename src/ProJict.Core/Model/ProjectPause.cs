namespace ProJict.Core.Model;

/// <summary>
/// A paused project's items wait (#e17bd61c): pausing it puts its open items on hold, each remembering the priority it
/// had, so agents leave them alone; making it Active, Released or an Idea again gives each one back its priority.
/// Items already on hold stay on hold, a sub-item without a priority of its own follows its parent's, and an item whose
/// priority you changed while the project was paused keeps your change. Archiving a paused project leaves its items
/// on hold, to come back when it's active again.
/// </summary>
public static class ProjectPause
{
    /// <summary>The project's status went from <paramref name="from"/> to <paramref name="to"/>: how many items it put on hold or gave back.</summary>
    public static int StatusChanged(Project project, ProjectStatus from, ProjectStatus to)
    {
        if (to == ProjectStatus.Paused) return from == ProjectStatus.Paused ? 0 : Hold(project);
        return to is ProjectStatus.Active or ProjectStatus.Released or ProjectStatus.Idea ? Resume(project) : 0;
    }

    /// <summary>Puts the project's open items on hold, each remembering its priority; how many.</summary>
    public static int Hold(Project project)
    {
        var held = 0;
        foreach (var line in project.Walk())
        {
            var item = line.Item;
            if (item.IsDone || item.Priority == Priority.Hold || item.PriorityBeforePause != null) continue;
            if (line.Parent != null && item.Priority == Priority.None) continue; // it follows its parent
            item.PriorityBeforePause = item.Priority;
            item.Priority = Priority.Hold;
            held++;
        }
        return held;
    }

    /// <summary>Gives the items pausing put on hold their priority back (those still on hold); how many.</summary>
    public static int Resume(Project project)
    {
        var back = 0;
        foreach (var item in project.Walk().Select(l => l.Item))
        {
            if (item.PriorityBeforePause is not { } before) continue;
            if (item.Priority == Priority.Hold)
            {
                item.Priority = before;
                back++;
            }
            item.PriorityBeforePause = null;
        }
        return back;
    }
}
