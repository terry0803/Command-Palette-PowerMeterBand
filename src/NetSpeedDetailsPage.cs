using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerMeterBand;

/// <summary>
/// Details page opened from the network Dock band: both speeds, the connected adapters,
/// and a button that switches which speed the Dock shows.
/// </summary>
internal sealed partial class NetSpeedDetailsPage : LiveCardPage
{
    private const string Template = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5",
          "body": [
            {
              "type": "ColumnSet",
              "columns": [
                {
                  "type": "Column",
                  "items": [
                    { "type": "TextBlock", "text": "上傳", "size": "small", "isSubtle": true },
                    { "type": "TextBlock", "text": "${upload}", "size": "extraLarge", "weight": "bolder" }
                  ]
                },
                {
                  "type": "Column",
                  "items": [
                    { "type": "TextBlock", "text": "下載", "size": "small", "isSubtle": true, "horizontalAlignment": "right" },
                    { "type": "TextBlock", "text": "${download}", "size": "extraLarge", "weight": "bolder", "horizontalAlignment": "right" }
                  ]
                }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "large",
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    { "type": "TextBlock", "text": "已連線的網卡", "size": "small", "isSubtle": true },
                    { "type": "TextBlock", "text": "${adapters}", "size": "medium", "wrap": true }
                  ]
                },
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    { "type": "TextBlock", "text": "Dock 顯示", "size": "small", "isSubtle": true, "horizontalAlignment": "right" },
                    { "type": "TextBlock", "text": "${direction}", "size": "medium", "horizontalAlignment": "right" }
                  ]
                }
              ]
            },
            {
              "type": "TextBlock",
              "spacing": "extraLarge",
              "text": "統計所有實體網卡，不含 VPN 和虛擬網卡。單位跟 PowerToys 內建的網速相同（1 Kbps = 1024 bps），Dock 上的 Kb、Mb、Gb 是 Kbps、Mbps、Gbps 的簡寫。每秒更新。",
              "size": "small",
              "isSubtle": true,
              "wrap": true
            }
          ]
        }
        """;

    public NetSpeedDetailsPage()
        : base(Template)
    {
        Id = "com.terry.powermeter.netspeed.details";
        Name = "查看詳細資訊";
        Title = "即時網速";
        Icon = NetSpeedDockBand.DownloadIcon;
        Commands =
        [
            new CommandContextItem(new ToggleNetSpeedDirectionCommand()),
            new CommandContextItem(new OpenUrlCommand("ms-settings:datausage") { Name = "開啟數據使用量設定" }),
        ];
        Update(new NetSpeedSnapshot(null, null, null)); // Replaced by the band's first reading right away.
    }

    public void Update(NetSpeedSnapshot s)
    {
        var data = new JsonObject
        {
            ["upload"] = NetSpeedFormat.BitsPerSecond(s.UploadBytesPerSecond),
            ["download"] = NetSpeedFormat.BitsPerSecond(s.DownloadBytesPerSecond),
            ["adapters"] = s.ConnectedAdapters switch
            {
                null => "無法讀取網卡資料",
                [] => "沒有連線",
                var adapters => string.Join("\n", adapters.Select(a => $"{a.Name}（{NetSpeedFormat.LinkSpeed(a.LinkSpeedBitsPerSecond)}）")),
            },
            ["direction"] = NetSpeedSettings.Describe(NetSpeedSettings.Direction),
        };

        Show(data.ToJsonString());
    }
}
