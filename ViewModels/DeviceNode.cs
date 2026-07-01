using System.Collections.ObjectModel;
using Avalonia.Media;
using LaserConsole.Models;
using static LaserConsole.Models.ComponentStatus;

namespace LaserConsole.ViewModels;

public enum DeviceNodeStatus { Unknown, Ok, Warning, Error }

public sealed class DeviceNode : ViewModelBase
{
    public string Name     { get; init; } = "";
    public string Role     { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string KeyHex   { get; init; } = "";

    DeviceNodeStatus _status;
    public DeviceNodeStatus NodeStatus
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
                OnPropertyChanged(nameof(StatusBrush));
        }
    }

    public IBrush StatusBrush => NodeStatus switch
    {
        DeviceNodeStatus.Ok      => new SolidColorBrush(Color.Parse("#A6E3A1")),
        DeviceNodeStatus.Warning => new SolidColorBrush(Color.Parse("#F9E2AF")),
        DeviceNodeStatus.Error   => new SolidColorBrush(Color.Parse("#F38BA8")),
        _                        => new SolidColorBrush(Color.Parse("#6C7086")),
    };

    public ObservableCollection<AttributeEntry> Attributes { get; } = new();
    public ObservableCollection<DeviceNode>     Children   { get; } = new();

    bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public bool HasAttributes => Attributes.Count > 0;

    public DeviceNode()
    {
        Attributes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAttributes));
    }

    public static DeviceNode FromInfo(DeviceInfo info)
    {
        var node = new DeviceNode
        {
            Name       = info.Name,
            Role       = info.Role,
            Endpoint   = info.Endpoint,
            KeyHex     = info.KeyHex,
            NodeStatus = info.Status switch
            {
                Ok      => DeviceNodeStatus.Ok,
                Warning => DeviceNodeStatus.Warning,
                Error   => DeviceNodeStatus.Error,
                _       => DeviceNodeStatus.Unknown,
            },
        };
        foreach (var (k, v) in info.Attributes)
            node.Attributes.Add(new AttributeEntry(k, v));
        foreach (var child in info.Children)
            node.Children.Add(FromInfo(child));
        return node;
    }
}

public sealed record AttributeEntry(string Name, string Value, string TypeInfo = "");
