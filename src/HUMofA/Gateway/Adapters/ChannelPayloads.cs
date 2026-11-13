using System.Text.Json.Serialization;

namespace HUMofA.Gateway.Adapters;

// =========================================================================
// 1. Telegram Payload Models (Telegram Bot API v7.0+)
// =========================================================================

public record TelegramInlineKeyboardButton(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("callback_data")] string? CallbackData = null,
    [property: JsonPropertyName("url")] string? Url = null
);

public record TelegramInlineKeyboard(
    [property: JsonPropertyName("inline_keyboard")] IReadOnlyList<IReadOnlyList<TelegramInlineKeyboardButton>> InlineKeyboard
);

public record TelegramMessagePayload(
    [property: JsonPropertyName("chat_id")] string ChatId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("parse_mode")] string? ParseMode = "MarkdownV2",
    [property: JsonPropertyName("reply_markup")] TelegramInlineKeyboard? ReplyMarkup = null
);

// =========================================================================
// 2. Discord Payload Models (Discord REST API / Webhooks v10)
// =========================================================================

public record DiscordEmbedFooter(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("icon_url")] string? IconUrl = null
);

public record DiscordEmbedField(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("inline")] bool Inline = false
);

public record DiscordEmbed(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("color")] int Color,
    [property: JsonPropertyName("fields")] IReadOnlyList<DiscordEmbedField>? Fields = null,
    [property: JsonPropertyName("footer")] DiscordEmbedFooter? Footer = null,
    [property: JsonPropertyName("timestamp")] string? Timestamp = null
);

public record DiscordButton(
    [property: JsonPropertyName("type")] int Type = 2, // 2 = Button
    [property: JsonPropertyName("style")] int Style = 1, // 1=Primary, 2=Secondary, 3=Success, 4=Danger
    [property: JsonPropertyName("label")] string Label = "",
    [property: JsonPropertyName("custom_id")] string CustomId = "",
    [property: JsonPropertyName("disabled")] bool Disabled = false
);

public record DiscordActionRow(
    [property: JsonPropertyName("type")] int Type = 1, // 1 = Action Row
    [property: JsonPropertyName("components")] IReadOnlyList<DiscordButton> Components = null!
);

public record DiscordMessagePayload(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("embeds")] IReadOnlyList<DiscordEmbed>? Embeds = null,
    [property: JsonPropertyName("components")] IReadOnlyList<DiscordActionRow>? Components = null
);

// =========================================================================
// 3. Slack Payload Models (Slack Web API & Block Kit)
// =========================================================================

public record SlackTextObject(
    [property: JsonPropertyName("type")] string Type = "plain_text",
    [property: JsonPropertyName("text")] string Text = "",
    [property: JsonPropertyName("emoji")] bool? Emoji = null
);

public record SlackButtonElement(
    [property: JsonPropertyName("type")] string Type = "button",
    [property: JsonPropertyName("text")] SlackTextObject Text = null!,
    [property: JsonPropertyName("action_id")] string ActionId = "",
    [property: JsonPropertyName("value")] string Value = "",
    [property: JsonPropertyName("style")] string? Style = null // "primary" or "danger"
);

public record SlackSectionBlock(
    [property: JsonPropertyName("type")] string Type = "section",
    [property: JsonPropertyName("text")] SlackTextObject Text = null!,
    [property: JsonPropertyName("fields")] IReadOnlyList<SlackTextObject>? Fields = null
);

public record SlackActionsBlock(
    [property: JsonPropertyName("type")] string Type = "actions",
    [property: JsonPropertyName("block_id")] string? BlockId = null,
    [property: JsonPropertyName("elements")] IReadOnlyList<SlackButtonElement> Elements = null!
);

public record SlackMessagePayload(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("blocks")] IReadOnlyList<object>? Blocks = null
);

// =========================================================================
// 4. WhatsApp Payload Models (Meta Graph API / WhatsApp Cloud API v19.0+)
// =========================================================================

public record WhatsAppTextObject(
    [property: JsonPropertyName("text")] string Text
);

public record WhatsAppHeaderObject(
    [property: JsonPropertyName("type")] string Type = "text",
    [property: JsonPropertyName("text")] string Text = ""
);

public record WhatsAppButtonReply(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title
);

public record WhatsAppButton(
    [property: JsonPropertyName("type")] string Type = "reply",
    [property: JsonPropertyName("reply")] WhatsAppButtonReply Reply = null!
);

public record WhatsAppButtonAction(
    [property: JsonPropertyName("buttons")] IReadOnlyList<WhatsAppButton> Buttons
);

public record WhatsAppInteractiveObject(
    [property: JsonPropertyName("type")] string Type = "button",
    [property: JsonPropertyName("header")] WhatsAppHeaderObject? Header = null,
    [property: JsonPropertyName("body")] WhatsAppTextObject Body = null!,
    [property: JsonPropertyName("footer")] WhatsAppTextObject? Footer = null,
    [property: JsonPropertyName("action")] WhatsAppButtonAction Action = null!
);

public record WhatsAppTextMessagePayload(
    [property: JsonPropertyName("messaging_product")] string MessagingProduct = "whatsapp",
    [property: JsonPropertyName("recipient_type")] string RecipientType = "individual",
    [property: JsonPropertyName("to")] string To = "",
    [property: JsonPropertyName("type")] string Type = "text",
    [property: JsonPropertyName("text")] WhatsAppTextObject Text = null!
);

public record WhatsAppInteractivePayload(
    [property: JsonPropertyName("messaging_product")] string MessagingProduct = "whatsapp",
    [property: JsonPropertyName("recipient_type")] string RecipientType = "individual",
    [property: JsonPropertyName("to")] string To = "",
    [property: JsonPropertyName("type")] string Type = "interactive",
    [property: JsonPropertyName("interactive")] WhatsAppInteractiveObject Interactive = null!
);

// =========================================================================
// 5. Signal Payload Models (Signal CLI JSON-RPC 2.0 daemon)
// =========================================================================

public record SignalSendParams(
    [property: JsonPropertyName("recipients")] IReadOnlyList<string> Recipients,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("attachments")] IReadOnlyList<string>? Attachments = null
);

public record SignalJsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc = "2.0",
    [property: JsonPropertyName("method")] string Method = "send",
    [property: JsonPropertyName("params")] SignalSendParams Params = null!,
    [property: JsonPropertyName("id")] string Id = ""
);

// =========================================================================
// 6. Email Payload Models (MIME & HTML Approval Card)
// =========================================================================

public record EmailAttachment(
    string FileName,
    string ContentType,
    byte[] Data
);

public record EmailMessagePayload(
    string From,
    IReadOnlyList<string> To,
    string Subject,
    string TextBody,
    string HtmlBody,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyList<EmailAttachment>? Attachments = null
);

// =========================================================================
// 7. CLI Payload Models (ANSI Console & Interactive Prompts)
// =========================================================================

public record CliMessagePayload(
    string Channel,
    string Sender,
    string Content,
    DateTime Timestamp
);

public record CliApprovalPayload(
    string RequestId,
    string ActionName,
    RiskTier Tier,
    string Description,
    IReadOnlyDictionary<string, string>? Parameters = null
);
