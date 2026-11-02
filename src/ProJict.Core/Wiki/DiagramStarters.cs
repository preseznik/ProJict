using System.Globalization;

namespace ProJict.Core.Wiki;

/// <summary>A diagram to start from: its name in the editor's Diagram menu and its ```mermaid block.</summary>
public sealed record DiagramStarter(string Name, string Text);

/// <summary>
/// The wiki editor's Diagram menu: one small, working diagram of each kind mermaid draws, to change into your own. A
/// Gantt chart starts today.
/// </summary>
public static class DiagramStarters
{
    public static IReadOnlyList<DiagramStarter> All(DateTime? today = null)
    {
        var day = (today ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // As the editor writes them: lines end in \n, whatever this file's endings.
        return [.. Starters(day).Select(s => s with { Text = s.Text.Replace("\r\n", "\n") })];
    }

    private static List<DiagramStarter> Starters(string day) =>
        [
            new("Flowchart", """
                ```mermaid
                flowchart LR
                    Idea[Idea] --> Build[Build]
                    Build --> Test{Works?}
                    Test -- yes --> Ship[Ship]
                    Test -- no --> Build
                ```
                """),
            new("Sequence", """
                ```mermaid
                sequenceDiagram
                    participant You
                    participant Agent
                    You->>Agent: Ask for a change
                    Agent-->>You: A plan
                    You->>Agent: Go ahead
                    Agent-->>You: Done, with screenshots
                ```
                """),
            new("Class", """
                ```mermaid
                classDiagram
                    class Project {
                        +string Name
                        +AddItem(text)
                    }
                    class Item {
                        +string Text
                        +bool Done
                    }
                    Project "1" --> "*" Item : has
                ```
                """),
            new("State", """
                ```mermaid
                stateDiagram-v2
                    [*] --> Todo
                    Todo --> InProgress : checked out
                    InProgress --> Done : completed
                    Done --> Todo : sent back
                    Done --> [*]
                ```
                """),
            new("Entity relationship", """
                ```mermaid
                erDiagram
                    PROJECT ||--o{ ITEM : has
                    ITEM ||--o{ COMMENT : has
                    PROJECT ||--o{ CHANNEL : "talks in"
                ```
                """),
            new("Gantt chart", $$"""
                ```mermaid
                gantt
                    title Next release
                    dateFormat YYYY-MM-DD
                    section Build
                    Feature   :a1, {{day}}, 3d
                    Tests     :after a1, 2d
                    section Ship
                    Release   :2d
                ```
                """),
            new("Mind map", """
                ```mermaid
                mindmap
                  root((Idea))
                    Why
                    How
                      Steps
                      Tools
                    Who
                ```
                """),
            new("Pie chart", """
                ```mermaid
                pie title Where the time went
                    "Building" : 50
                    "Testing" : 30
                    "Talking" : 20
                ```
                """),
        ];

    /// <summary>The diagram's own text, without the ```mermaid fence around it.</summary>
    public static string Source(DiagramStarter starter)
    {
        var lines = starter.Text.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Skip(1).Take(lines.Length - 2));
    }
}
