using System.Globalization;
using System.Text.Json;
using Xunit;

namespace GraphTaskSynchronizer.Tests;

public sealed class ExtendedPropertyIdTests
{
    [Theory]
    [InlineData("Double {00062003-0000-0000-c000-000000000046} Id 0x8102")]
    [InlineData("Double {00062003-0000-0000-C000-000000000046} Id 0x00008102")]
    [InlineData("double {00062003-0000-0000-C000-000000000046} id 0x8102")]
    [InlineData("Double 00062003-0000-0000-C000-000000000046 Id 0x8102")]
    public void PercentComplete_matches_however_Graph_spells_it(string returned)
    {
        Assert.True(ExtendedPropertyId.AreEqual(TaskProperties.PercentComplete, returned));
    }

    [Fact]
    public void A_different_type_or_id_does_not_match()
    {
        Assert.False(ExtendedPropertyId.AreEqual(TaskProperties.PercentComplete,
            "Integer {00062003-0000-0000-C000-000000000046} Id 0x8102"));
        Assert.False(ExtendedPropertyId.AreEqual(TaskProperties.PercentComplete,
            "Double {00062003-0000-0000-C000-000000000046} Id 0x8101"));
    }

    [Fact]
    public void Tagged_properties_ignore_hex_width_and_case()
    {
        Assert.True(ExtendedPropertyId.AreEqual("String 0x001A", "String 0x1a"));
    }

    [Fact]
    public void The_task_query_selects_the_typed_fields_and_expands_only_PercentComplete()
    {
        var decoded = Uri.UnescapeDataString(TaskProperties.TaskQuery);

        Assert.StartsWith("$select=id,status,", decoded);
        Assert.Contains("startDateTime,dueDateTime,completedDateTime", decoded);
        Assert.EndsWith(
            "&$expand=singleValueExtendedProperties($filter=id eq 'Double {00062003-0000-0000-C000-000000000046} Id 0x8102')",
            decoded);
        Assert.DoesNotContain(" ", TaskProperties.TaskQuery);   // encoded for the URL
    }
}

public sealed class TaskSnapshotFactoryTests
{
    private static TodoTaskDto Parse(string json) =>
        JsonSerializer.Deserialize<TodoTaskDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void PercentComplete_comes_from_the_extended_property_and_the_rest_from_typed_fields()
    {
        var json = TodoJson.Task("task-1", status: "inProgress", percent: 0.75, dueDate: "2026-10-31T00:00:00.0000000");

        var snapshot = TaskSnapshotFactory.FromTodoTask(Parse(json));

        Assert.Equal("task-1", snapshot.Id);
        Assert.Equal(0.75, snapshot.PercentComplete);
        Assert.Equal(TodoTaskStatus.InProgress, snapshot.Status);
        Assert.Equal(new TodoDateTime("2026-10-31T00:00:00", "UTC"), snapshot.DueDateTime);
        Assert.Null(snapshot.StartDateTime);
        Assert.Null(snapshot.CompletedDateTime);
    }

    [Fact]
    public void A_completed_task_carries_its_typed_status_and_completedDateTime()
    {
        var json = TodoJson.Task("task-1", status: "completed", percent: 1.0, completedDate: "2026-10-28T00:00:00.0000000");

        var snapshot = TaskSnapshotFactory.FromTodoTask(Parse(json));

        Assert.Equal(TodoTaskStatus.Completed, snapshot.Status);
        Assert.Equal(1.0, snapshot.PercentComplete);
        Assert.Equal(new TodoDateTime("2026-10-28T00:00:00", "UTC"), snapshot.CompletedDateTime);
    }

    [Fact]
    public void A_task_without_the_extended_property_has_no_PercentComplete()
    {
        var snapshot = TaskSnapshotFactory.FromTodoTask(Parse(TodoJson.Task("task-1", percent: null)));

        Assert.Null(snapshot.PercentComplete);
        Assert.Equal(TodoTaskStatus.InProgress, snapshot.Status);
    }

    [Fact]
    public void Title_etag_importance_and_categories_are_mapped()
    {
        var snapshot = TaskSnapshotFactory.FromTodoTask(Parse(TodoJson.Task("task-1", title: "Renew licences")));

        Assert.Equal("Renew licences", snapshot.Subject);
        Assert.Equal("W/\"vVxzmGwEk0GWDaBVxDVVJQAAAhFAAg==\"", snapshot.ChangeKey);
        Assert.Equal("normal", snapshot.Importance);
        Assert.Equal("Finance", Assert.Single(snapshot.Categories));
        Assert.Equal(DateTimeOffset.Parse("2026-10-28T00:23:45.1234567Z"), snapshot.LastModifiedDateTime);
    }

    [Fact]
    public void PercentComplete_is_parsed_with_the_invariant_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");   // decimal comma

            var snapshot = TaskSnapshotFactory.FromTodoTask(Parse(TodoJson.Task("task-1", percent: 0.75)));

            Assert.Equal(0.75, snapshot.PercentComplete);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("2026-10-31T00:00:00.0000000", "UTC", "2026-10-31T00:00:00", "UTC")]
    [InlineData("2026-10-30T14:00:00", "UTC", "2026-10-30T14:00:00", "UTC")]
    [InlineData("2026-10-31T09:30:00.0000000", "E. Australia Standard Time", "2026-10-31T09:30:00", "E. Australia Standard Time")]
    public void DateTimeTimeZone_values_are_kept_as_Graph_returned_them(string dateTime, string zone, string expectedDateTime, string expectedZone)
    {
        var value = TaskSnapshotFactory.ParseTodoDateTime(new DateTimeTimeZoneDto { DateTime = dateTime, TimeZone = zone });

        Assert.Equal(new TodoDateTime(expectedDateTime, expectedZone), value);
    }

    [Fact]
    public void A_missing_dateTimeTimeZone_is_null()
    {
        Assert.Null(TaskSnapshotFactory.ParseTodoDateTime(null));
        Assert.Null(TaskSnapshotFactory.ParseTodoDateTime(new DateTimeTimeZoneDto()));
    }

    [Theory]
    [InlineData("notStarted", TodoTaskStatus.NotStarted)]
    [InlineData("inProgress", TodoTaskStatus.InProgress)]
    [InlineData("completed", TodoTaskStatus.Completed)]
    [InlineData("waitingOnOthers", TodoTaskStatus.WaitingOnOthers)]
    [InlineData("deferred", TodoTaskStatus.Deferred)]
    public void To_Do_status_values_are_parsed(string todoStatus, TodoTaskStatus expected)
    {
        Assert.Equal(expected, TaskSnapshotFactory.ParseTodoStatus(todoStatus));
    }

    [Theory]
    [InlineData(0.0, "0%")]
    [InlineData(0.25, "25%")]
    [InlineData(1.0, "100%")]
    [InlineData(0.333, "33.3%")]
    public void PercentComplete_is_shown_as_a_percentage(double value, string expected)
    {
        Assert.Equal(expected, TaskFormat.Percent(value));
    }
}
