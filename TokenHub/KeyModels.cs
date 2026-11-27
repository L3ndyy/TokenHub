using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TokenHub.Services;

namespace TokenHub;

/// <summary>Shape of an exported key file, so a bundle can be typed instead of anonymous.</summary>
internal sealed class KeyRecord
{
    public string? Key { get; set; }

    public string? Label { get; set; }

    public bool Enabled { get; set; } = true;
}

internal sealed class KeyBundle
{
    public int Version { get; set; } = 1;

    public DateTime ExportedUtc { get; set; }

    public List<KeyRecord> Keys { get; set; } = new();

    /// <summary>Project model overrides travel with the keys, so a new machine is set up in one import.</summary>
    public List<ProjectProfile> Profiles { get; set; } = new();
}
