using System.Text.Json.Nodes;
using Waypoint;

namespace Waypoint.Tests;

public class SettingsFormTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waypoint-settings-" + Guid.NewGuid().ToString("N"));
    const string Schema = """
        {"version":1,"renamed":{"oldRate":"rate"},"settings":{
          "enabled":{"type":"bool","default":true},
          "rate":{"type":"number","default":0.5,"min":0,"max":1,"step":0.1,"format":"percent"},
          "count":{"type":"integer","default":2,"min":0,"max":10},
          "interval":{"type":"range","of":"integer","default":[1,3],"min":0,"max":10},
          "mode":{"type":"choice","default":"a","options":[{"value":"a","label":"First"},"b"]},
          "areas":{"type":"choices","default":["one"],"source":"area","maxItems":3},
          "name":{"type":"text","default":"abc","pattern":"[a-z]+","maxLength":8},
          "weights":{"type":"list","default":[1],"item":{"type":"number","min":0,"max":5},"maxItems":3},
          "bonuses":{"type":"map","default":{"a":1},"options":["a","b"],"value":{"type":"integer","min":0}},
          "advanced":{"type":"group","advanced":true,"when":{"enabled":true},"settings":{
            "delay":{"type":"number","default":2,"format":"duration","min":0},
            "nested":{"type":"group","settings":{"active":{"type":"bool","default":false}}}
          }}
        }}
        """;
    ModSettingsForm Load(string? user = null)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), Schema);
        if (user is not null)
        {
            Directory.CreateDirectory(Path.Combine(_root, "data", "modules"));
            File.WriteAllText(Path.Combine(_root, "data", "modules", "example.json"), user);
        }
        return ModSettingsForm.Load("example", Path.Combine(_root, "settings.json"), _root, "modules");
    }
    [Theory]
    [InlineData("enabled", "true", "1")]
    [InlineData("rate", "0.7", "0.75")]
    [InlineData("count", "3", "3.5")]
    [InlineData("interval", "[2,5]", "[7,2]")]
    [InlineData("mode", "\"b\"", "\"unknown\"")]
    [InlineData("areas", "[\"one\",\"two\"]", "[\"one\",\"one\"]")]
    [InlineData("name", "\"hello\"", "\"123\"")]
    [InlineData("weights", "[1,2]", "[1,6]")]
    [InlineData("bonuses", "{\"b\":2}", "{\"bad\":2}")]
    [InlineData("advanced", "{\"delay\":5}", "{\"delay\":-1}")]
    public void All_declared_types_accept_valid_values_and_reject_invalid_values(string name, string valid, string invalid)
    {
        var setting = Load().Settings.Single(s => s.Name == name);
        Assert.Null(ModSettingsForm.Validate(setting, JsonNode.Parse(valid)));
        Assert.NotNull(ModSettingsForm.Validate(setting, JsonNode.Parse(invalid)));
    }
    [Fact]
    public void Saving_and_resetting_keep_unknown_values_in_nested_groups_and_apply_renames()
    {
        var form = Load("""{"oldRate":0.7,"future":123,"advanced":{"delay":6,"futureNested":42,"nested":{"active":true,"unknown":7}}}""");
        Assert.Equal(0.7, form.Current(form.Settings.Single(s => s.Name == "rate"))!.GetValue<double>());
        var defaults = form.Settings.ToDictionary(s => s.Name, s => s.Default?.DeepClone());
        var saved = form.Values(defaults);
        Assert.False(saved.ContainsKey("oldRate"));
        Assert.False(saved.ContainsKey("rate"));
        Assert.Equal(123, saved["future"]!.GetValue<int>());
        Assert.Equal(42, saved["advanced"]!["futureNested"]!.GetValue<int>());
        Assert.Equal(7, saved["advanced"]!["nested"]!["unknown"]!.GetValue<int>());
        Assert.Null(saved["advanced"]!["delay"]);
    }
    [Fact]
    public void Unreadable_user_settings_are_preserved_and_block_editing()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Load("broken json"));
        Assert.Equal("broken json", File.ReadAllText(Path.Combine(_root, "data", "modules", "example.json")));
        Assert.Throws<InvalidDataException>(() => Load("[]"));
    }
    [Fact]
    public void Percent_input_round_trips_with_steps_and_kind_paths_are_distinct()
    {
        var setting = Load().Settings.Single(s => s.Name == "rate");
        var value = ModSettingsForm.Read(setting, "70", out var error);
        Assert.Null(error); Assert.Equal(0.7, value!.GetValue<double>());
        Assert.Equal("70", ModSettingsForm.Show(value, setting));
        Assert.NotEqual(ModSettingsForm.UserFile("example"), ModSettingsForm.UserFile("example", "modules"));
        Assert.Throws<ArgumentException>(() => ModSettingsForm.UserFile("example", "../outside"));
    }
    [Fact]
    public void Numeric_display_keeps_precision_beyond_six_decimal_places()
    {
        var setting = Load().Settings.Single(s => s.Name == "count") with { Type = "number", Min = null, Max = null, Step = null };
        var original = JsonValue.Create(0.123456789012345);
        var value = ModSettingsForm.Read(setting, ModSettingsForm.Show(original, setting), out var error);
        Assert.Null(error);
        Assert.True(ModSettingsForm.Equal(original, value));
    }
    [Fact]
    public void Editor_sliders_and_exact_values_share_the_same_validated_value()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var form = Load(); var editor = new SettingsEditor(form);
                Assert.True(editor.Children.Count > 0);
                var values = editor.Values();
                Assert.Empty(values);
                Assert.False(editor.Dirty);
                IEnumerable<System.Windows.DependencyObject> Walk(System.Windows.DependencyObject root)
                {
                    yield return root;
                    foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
                        foreach (var nested in Walk(child)) yield return nested;
                }
                var controls = Walk(editor).ToArray();
                var advanced = controls.OfType<System.Windows.Controls.CheckBox>().First();
                advanced.IsChecked = true;
                Assert.False(editor.Dirty);
                var slider = controls.OfType<System.Windows.Controls.Slider>().First();
                slider.Value = 80;
                Assert.Equal(0.8, editor.Values()["rate"]!.GetValue<double>());
                Assert.True(editor.Dirty);
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        Assert.Null(failure);
    }
    [Fact]
    public void A_narrow_card_puts_its_control_under_its_text_and_a_wide_one_beside_it()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var editor = new SettingsEditor(Load());
                IEnumerable<System.Windows.DependencyObject> Walk(System.Windows.DependencyObject root)
                {
                    yield return root;
                    foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
                        foreach (var nested in Walk(child)) yield return nested;
                }
                var row = Walk(editor).OfType<System.Windows.Controls.Grid>().First();
                var control = row.Children.OfType<System.Windows.Controls.StackPanel>().Last();
                void Lay(double width)
                {
                    editor.Measure(new System.Windows.Size(width, 5000));
                    editor.Arrange(new System.Windows.Rect(0, 0, width, 5000));
                    editor.UpdateLayout();
                }
                Lay(400);
                Assert.Equal((1, 0), (System.Windows.Controls.Grid.GetRow(control), System.Windows.Controls.Grid.GetColumn(control)));
                Assert.True(double.IsNaN(control.Width));
                Lay(900);
                Assert.Equal((0, 1), (System.Windows.Controls.Grid.GetRow(control), System.Windows.Controls.Grid.GetColumn(control)));
                Assert.False(double.IsNaN(control.Width));
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        Assert.Null(failure);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
