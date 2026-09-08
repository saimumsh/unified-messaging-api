using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedMessaging.Api.Adapters.LinkedIn;
using UnifiedMessaging.Api.Models;

namespace UnifiedMessaging.Api.Tests;

public class LinkedInAdapterTests
{
    private static readonly LinkedInAdapter Adapter =
        new(connector: null!, NullLogger<LinkedInAdapter>.Instance);

    private static JsonElement Payload(string rawJson, string? accountId = null)
    {
        var acc = accountId ?? Guid.NewGuid().ToString();
        return JsonDocument.Parse($$"""
        { "accountId": "{{acc}}", "raw": {{rawJson}} }
        """).RootElement;
    }

    [Fact]
    public void Parses_plain_inbound_message()
    {
        var msg = Adapter.ParseIncoming(Payload("""
        {
          "threadId": "2-abc==",
          "eventUrn": "urn:li:msg_event:(urn:li:msg_conversation:(x),1)",
          "from": "urn:li:fsd_profile:AAA",
          "fromMe": false,
          "body": { "text": "hey there" },
          "attachments": [],
          "createdAt": 1757082000000
        }
        """))!;

        Assert.Equal("hey there", msg.Text);
        Assert.Equal("2-abc==", msg.ChatId);
        Assert.Equal("urn:li:fsd_profile:AAA", msg.SenderId);
        Assert.Equal("urn:li:msg_event:(urn:li:msg_conversation:(x),1)", msg.ProviderMessageId);
        Assert.Equal(MessageDirection.Inbound, msg.Direction);
        Assert.Equal(new DateTime(2025, 9, 5, 14, 20, 0, DateTimeKind.Utc), msg.Timestamp);
    }

    [Fact]
    public void Marks_own_messages_outbound_with_me_sender()
    {
        var msg = Adapter.ParseIncoming(Payload("""
        { "threadId": "2-abc==", "eventUrn": "urn:li:x:1", "fromMe": true, "body": { "text": "my reply" } }
        """))!;

        Assert.Equal(MessageDirection.Outbound, msg.Direction);
        Assert.Equal("me", msg.SenderId);
    }

    [Fact]
    public void Falls_back_to_subject_when_no_body_text()
    {
        var msg = Adapter.ParseIncoming(Payload("""
        { "threadId": "2-abc==", "eventUrn": "urn:li:x:2", "fromMe": false, "subject": "InMail: coffee?" }
        """))!;

        Assert.Equal("InMail: coffee?", msg.Text);
    }

    [Fact]
    public void Maps_attachments()
    {
        var msg = Adapter.ParseIncoming(Payload("""
        {
          "threadId": "2-abc==", "eventUrn": "urn:li:x:3", "fromMe": false,
          "body": { "text": "" },
          "attachments": [
            { "type": "image", "url": "https://media.licdn.com/x.png", "mimeType": "image/png", "name": "x.png", "byteSize": 4096 }
          ]
        }
        """))!;

        var att = Assert.Single(msg.Attachments);
        Assert.Equal("image", att.Type);
        Assert.Equal("https://media.licdn.com/x.png", att.Url);
        Assert.Equal("image/png", att.MimeType);
        Assert.Equal("x.png", att.FileName);
        Assert.Equal(4096, att.SizeBytes);
    }

    [Fact]
    public void Returns_null_for_content_free_event()
    {
        Assert.Null(Adapter.ParseIncoming(Payload("""
        { "threadId": "2-abc==", "eventUrn": "urn:li:x:4", "fromMe": false, "body": { "text": "" }, "attachments": [] }
        """)));
    }

    [Fact]
    public void Returns_null_when_threadId_missing()
    {
        Assert.Null(Adapter.ParseIncoming(Payload("""
        { "eventUrn": "urn:li:x:5", "body": { "text": "orphan" } }
        """)));
    }

    [Fact]
    public void Returns_null_when_accountId_not_a_guid()
    {
        Assert.Null(Adapter.ParseIncoming(Payload(
            """{ "threadId": "2-abc==", "body": { "text": "hi" } }""", accountId: "not-a-guid")));
    }
}
