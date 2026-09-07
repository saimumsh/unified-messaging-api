using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedMessaging.Api.Adapters.WhatsApp;
using UnifiedMessaging.Api.Contracts;
using UnifiedMessaging.Api.Models;

namespace UnifiedMessaging.Api.Tests;

public class WhatsAppAdapterTests
{
    private static readonly WhatsAppAdapter Adapter =
        new(connector: null!, NullLogger<WhatsAppAdapter>.Instance);

    private static ConnectorMessageEvent Event(string json)
    {
        var accountId = Guid.NewGuid().ToString();
        var raw = JsonDocument.Parse(json).RootElement;
        return new ConnectorMessageEvent(accountId, raw);
    }

    [Fact]
    public void Parses_plain_conversation_message()
    {
        var evt = Event("""
        {
          "key": { "remoteJid": "49111@s.whatsapp.net", "fromMe": false, "id": "ABC123" },
          "messageTimestamp": 1757082000,
          "message": { "conversation": "hi there" }
        }
        """);

        var msg = Adapter.ParseMessageEvent(evt)!;

        Assert.Equal("hi there", msg.Text);
        Assert.Equal("49111@s.whatsapp.net", msg.ChatId);
        Assert.Equal("ABC123", msg.ProviderMessageId);
        Assert.Equal(MessageDirection.Inbound, msg.Direction);
        Assert.Equal(new DateTime(2025, 9, 5, 14, 20, 0, DateTimeKind.Utc), msg.Timestamp);
    }

    [Fact]
    public void Parses_extended_text_message()
    {
        var evt = Event("""
        {
          "key": { "remoteJid": "49111@s.whatsapp.net", "fromMe": false, "id": "X" },
          "message": { "extendedTextMessage": { "text": "a quoted reply" } }
        }
        """);

        Assert.Equal("a quoted reply", Adapter.ParseMessageEvent(evt)!.Text);
    }

    [Fact]
    public void Maps_image_message_with_caption_to_attachment()
    {
        var evt = Event("""
        {
          "key": { "remoteJid": "49111@s.whatsapp.net", "fromMe": false, "id": "IMG" },
          "message": { "imageMessage": { "mimetype": "image/jpeg", "caption": "look", "fileLength": 2048 } }
        }
        """);

        var msg = Adapter.ParseMessageEvent(evt)!;

        Assert.Equal("look", msg.Text);
        var att = Assert.Single(msg.Attachments);
        Assert.Equal("image", att.Type);
        Assert.Equal("image/jpeg", att.MimeType);
        Assert.Equal(2048, att.SizeBytes);
    }

    [Fact]
    public void Group_message_uses_participant_as_sender()
    {
        var evt = Event("""
        {
          "key": {
            "remoteJid": "12345-67890@g.us",
            "participant": "49999@s.whatsapp.net",
            "fromMe": false, "id": "G1"
          },
          "message": { "conversation": "group hi" }
        }
        """);

        var msg = Adapter.ParseMessageEvent(evt)!;
        Assert.Equal("12345-67890@g.us", msg.ChatId);
        Assert.Equal("49999@s.whatsapp.net", msg.SenderId);
    }

    [Fact]
    public void Handles_string_fileLength_and_sibling_context_info()
    {
        // Real Baileys shape: fileLength is a STRING, messageContextInfo is a sibling.
        var evt = Event("""
        {
          "key": { "remoteJid": "4067719929859@lid", "fromMe": false, "id": "V9" },
          "messageTimestamp": 1757226846,
          "message": {
            "videoMessage": { "mimetype": "video/mp4", "fileLength": "123456", "seconds": 8 },
            "messageContextInfo": { "deviceListMetadata": {} }
          }
        }
        """);

        var msg = Adapter.ParseMessageEvent(evt)!;
        var att = Assert.Single(msg.Attachments);
        Assert.Equal("video", att.Type);
        Assert.Equal(123456, att.SizeBytes);
        Assert.Equal(new DateTime(2025, 9, 7, 6, 34, 6, DateTimeKind.Utc), msg.Timestamp);
    }

    [Fact]
    public void Unwraps_ephemeral_wrapper_around_an_image()
    {
        var evt = Event("""
        {
          "key": { "remoteJid": "49111@s.whatsapp.net", "fromMe": false, "id": "E1" },
          "message": { "ephemeralMessage": { "message": {
            "imageMessage": { "mimetype": "image/jpeg", "caption": "hi" }
          }}}
        }
        """);

        var msg = Adapter.ParseMessageEvent(evt)!;
        Assert.Equal("hi", msg.Text);
        Assert.Equal("image", Assert.Single(msg.Attachments).Type);
    }

    [Fact]
    public void Unwraps_documentWithCaptionMessage()
    {
        var evt = Event("""
        {
          "key": { "remoteJid": "49111@s.whatsapp.net", "fromMe": false, "id": "E2" },
          "message": { "documentWithCaptionMessage": { "message": {
            "documentMessage": { "mimetype": "application/pdf", "fileName": "report.pdf", "caption": "q3" }
          }}}
        }
        """);

        var msg = Adapter.ParseMessageEvent(evt)!;
        Assert.Equal("q3", msg.Text);
        var att = Assert.Single(msg.Attachments);
        Assert.Equal("document", att.Type);
        Assert.Equal("report.pdf", att.FileName);
    }

    [Fact]
    public void Returns_null_for_non_content_message()
    {
        var evt = Event("""
        { "key": { "remoteJid": "49111@s.whatsapp.net", "id": "R" }, "message": { "protocolMessage": {} } }
        """);

        Assert.Null(Adapter.ParseMessageEvent(evt));
    }

    [Fact]
    public void Parses_reply_quote_and_mentions()
    {
        var evt = Event("""
        {
          "key": { "remoteJid": "49111@s.whatsapp.net", "id": "R1" },
          "message": { "extendedTextMessage": {
            "text": "@49999 look",
            "contextInfo": {
              "stanzaId": "ORIG1",
              "participant": "49111@s.whatsapp.net",
              "quotedMessage": { "conversation": "the original" },
              "mentionedJid": ["49999@s.whatsapp.net"]
            }
          }}
        }
        """);

        var m = Adapter.ParseMessageEvent(evt)!;
        Assert.Equal("ORIG1", m.ReplyToMessageId);
        Assert.Equal("the original", m.ReplyToText);
        Assert.Equal("49111@s.whatsapp.net", m.ReplyToSenderId);
        Assert.Equal(new[] { "49999@s.whatsapp.net" }, m.Mentions);
    }

    [Fact]
    public void Parses_a_poll_creation_message()
    {
        var evt = Event("""
        {
          "key": { "remoteJid": "12-34@g.us", "id": "P1" },
          "message": { "pollCreationMessageV3": {
            "name": "Lunch?", "options": [{ "optionName": "Pizza" }, { "optionName": "Sushi" }]
          }}
        }
        """);

        var m = Adapter.ParseMessageEvent(evt)!;
        Assert.Equal("Lunch?", m.PollName);
        Assert.Equal("Lunch?", m.Text);
        Assert.Equal(new[] { "Pizza", "Sushi" }, m.PollOptions);
    }

    [Fact]
    public void Parses_a_group_event_payload()
    {
        var payload = JsonDocument.Parse("""
        { "accountId": "11111111-1111-1111-1111-111111111111",
          "raw": { "groupEvent": {
            "jid": "12-34@g.us", "type": "promote",
            "participants": ["a@s.whatsapp.net", "b@s.whatsapp.net"], "author": "c@s.whatsapp.net" } } }
        """).RootElement;

        Assert.True(WhatsAppAdapter.IsGroupEvent(payload));
        var g = Adapter.ParseGroupEvent(payload)!;
        Assert.Equal("12-34@g.us", g.GroupJid);
        Assert.Equal("promote", g.Type);
        Assert.Equal(2, g.Participants.Length);
        Assert.Equal("c@s.whatsapp.net", g.Actor);
    }

    [Fact]
    public void Parses_a_poll_vote_payload()
    {
        var payload = JsonDocument.Parse("""
        { "accountId": "11111111-1111-1111-1111-111111111111",
          "raw": { "pollVote": {
            "chatId": "12-34@g.us", "pollMsgId": "P1",
            "voter": "x@s.whatsapp.net", "selectedOptions": ["Pizza"] } } }
        """).RootElement;

        Assert.True(WhatsAppAdapter.IsPollVote(payload));
        var v = Adapter.ParsePollVote(payload)!;
        Assert.Equal("P1", v.PollMessageId);
        Assert.Equal("x@s.whatsapp.net", v.VoterId);
        Assert.Equal(new[] { "Pizza" }, v.SelectedOptions);
    }

    [Fact]
    public void Detects_and_parses_a_reaction_add()
    {
        var payload = JsonDocument.Parse("""
        {
          "accountId": "11111111-1111-1111-1111-111111111111",
          "raw": { "reactionEvent": {
            "key": { "remoteJid": "49111@s.whatsapp.net", "fromMe": true, "id": "TGT1" },
            "reaction": { "text": "🔥" }
          }}
        }
        """).RootElement;

        Assert.True(WhatsAppAdapter.IsReaction(payload));
        var r = Adapter.ParseReaction(payload)!;
        Assert.Equal("🔥", r.Emoji);
        Assert.False(r.Removed);
        Assert.True(r.OnYourMessage);
        Assert.Equal("TGT1", r.TargetMessageId);
        Assert.Equal("49111@s.whatsapp.net", r.ChatId);
    }

    [Fact]
    public void Reaction_from_upsert_path_resolves_the_reactor()
    {
        var payload = JsonDocument.Parse("""
        {
          "accountId": "11111111-1111-1111-1111-111111111111",
          "raw": { "reactionEvent": {
            "key": { "remoteJid": "group-1@g.us", "fromMe": true, "id": "TGT" },
            "reaction": { "text": "👍" },
            "reactorKey": { "remoteJid": "group-1@g.us", "fromMe": false, "participant": "4999@s.whatsapp.net" }
          }}
        }
        """).RootElement;

        var r = Adapter.ParseReaction(payload)!;
        Assert.Equal("👍", r.Emoji);
        Assert.True(r.OnYourMessage);
        Assert.Equal("4999@s.whatsapp.net", r.ReactedBy);
        Assert.False(r.ReactedByMe);
    }

    [Fact]
    public void Empty_reaction_text_means_removed()
    {
        var payload = JsonDocument.Parse("""
        {
          "accountId": "11111111-1111-1111-1111-111111111111",
          "raw": { "reactionEvent": {
            "key": { "remoteJid": "49111@s.whatsapp.net", "fromMe": false, "id": "TGT2" },
            "reaction": { "text": "" }
          }}
        }
        """).RootElement;

        var r = Adapter.ParseReaction(payload)!;
        Assert.True(r.Removed);
        Assert.Null(r.Emoji);
        Assert.False(r.OnYourMessage);
    }

    [Fact]
    public void ParseIncoming_merges_connector_media_url_into_the_attachment()
    {
        var payload = JsonDocument.Parse("""
        {
          "accountId": "11111111-1111-1111-1111-111111111111",
          "raw": {
            "key": { "remoteJid": "49111@s.whatsapp.net", "fromMe": false, "id": "V1" },
            "message": { "videoMessage": { "mimetype": "video/mp4", "caption": "clip" } }
          },
          "media": { "type": "video", "url": "http://localhost:3001/media/acc/V1.mp4", "mimetype": "video/mp4", "size": 40000 }
        }
        """).RootElement;

        var msg = Adapter.ParseIncoming(payload)!;

        Assert.Equal("clip", msg.Text);
        var att = Assert.Single(msg.Attachments);
        Assert.Equal("video", att.Type);
        Assert.Equal("http://localhost:3001/media/acc/V1.mp4", att.Url);
        Assert.Equal(40000, att.SizeBytes);
    }

    [Fact]
    public void ParseIncoming_adds_an_attachment_when_baileys_node_was_absent()
    {
        var payload = JsonDocument.Parse("""
        {
          "accountId": "11111111-1111-1111-1111-111111111111",
          "raw": {
            "key": { "remoteJid": "49111@s.whatsapp.net", "fromMe": false, "id": "D1" },
            "message": { "conversation": "see attached" }
          },
          "media": { "type": "document", "url": "http://x/d.pdf", "fileName": "d.pdf" }
        }
        """).RootElement;

        var msg = Adapter.ParseIncoming(payload)!;
        var att = Assert.Single(msg.Attachments);
        Assert.Equal("document", att.Type);
        Assert.Equal("d.pdf", att.FileName);
    }

    [Fact]
    public void A_normal_message_is_not_a_reaction()
    {
        var payload = JsonDocument.Parse("""
        { "accountId": "x", "raw": {
            "key": { "remoteJid": "49111@s.whatsapp.net", "id": "M" },
            "message": { "conversation": "hi" } } }
        """).RootElement;

        Assert.False(WhatsAppAdapter.IsReaction(payload));
    }

    [Fact]
    public void Outbound_flag_sets_direction_and_me_sender()
    {
        var evt = Event("""
        {
          "key": { "remoteJid": "49111@s.whatsapp.net", "fromMe": true, "id": "O1" },
          "message": { "conversation": "sent from phone" }
        }
        """);

        var msg = Adapter.ParseMessageEvent(evt)!;
        Assert.Equal(MessageDirection.Outbound, msg.Direction);
        Assert.Equal("me", msg.SenderId);
    }
}
