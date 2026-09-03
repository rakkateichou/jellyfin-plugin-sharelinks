# ShareLinks for Jellyfin

This fork adds compact `/j/<code>` invitations and server-side watch-party room/media routing for
[rakkateichou/JellyWatchParty](https://github.com/rakkateichou/JellyWatchParty). It remains useful as
a standalone secure sharing plugin. The original project is
[Franciskid/jellyfin-plugin-sharelinks](https://github.com/Franciskid/jellyfin-plugin-sharelinks).

Send someone one movie, episode, season or series through a link that expires. They need no
account, and they see nothing else on your server.

`Jellyfin 10.11` · `.NET 9` · `no account for the guest` · `server-side confinement` · `automatic teardown`

ShareLinks adds a **ShareLink** entry to the context menu of any movie, series, season or episode.
You pick an expiry and receive a URL. The person who opens it lands on that title, already signed
in, with a temporary account that Jellyfin restricts to the shared branch. When the link expires,
the account and the temporary tag are removed.

<img width="1505" height="820" alt="Create guest link dialog, opened from the item menu" src="https://github.com/user-attachments/assets/27296f27-9a37-4870-90aa-df8b6d9e9f43" />

---

## Threat model

A guest holds a real Jellyfin access token. That token works in curl, in the mobile apps, and in
any other client. So a share feature that hides buttons in the browser protects nothing.

The confinement therefore lives on the server, in two layers:

1. **Jellyfin's tag policy.** Every share creates a random tag, `sharelinks-<32 hex>`. The tag goes
   on the shared item and on everything below it. The guest account allows exactly that one tag, so
   every other item, library and search returns empty from the API.
2. **A request filter for plugin routes.** Jellyfin's own API is bounded by the tag policy, and
   playback depends on it, so it stays open. Every other controller belongs to a plugin and is
   refused with 403 for guest accounts, unless an administrator opts that plugin in.

The web client lockdown is a third layer, and it is the only cosmetic one. It hides the home, menu
and search controls, makes cast and genre links inert, and returns the guest to the shared title if
they navigate out of the branch. A guest who disables the script reaches the home screen and finds
it empty, because the server answers those queries, not the browser.

No server-side setting pins a Jellyfin user to one page. The alternative is a parallel user system
with its own login flow inside the plugin, which is not maintainable and adds nothing: the tag
policy already decides what the guest can pull.

## Flow

```mermaid
sequenceDiagram
  participant A as Admin
  participant P as ShareLinks plugin
  participant J as Jellyfin core
  participant G as Guest browser
  A->>P: ShareLink on an item, pick expiry
  P->>P: 128-bit short code, store HMAC hash only
  P->>J: Tag the item and its children
  P-->>A: URL (returned once, copied to clipboard)
  G->>P: GET /j/&lt;code&gt;
  P->>P: Hash the token, look the record up
  P->>J: Create guest user, policy = allow that tag
  P->>J: AuthenticateDirect, mint a session
  P-->>G: Bootstrap page, lands on the title
  Note over P,J: On expiry or revoke: delete the guest,<br/>delete its devices, strip the tag from the tree
```

1. **Create.** You choose an expiry from 1 hour to 7 days, or an exact date, capped by the
   configured maximum. You also choose single use, which is the default, or multi use.
2. **Tag.** The plugin tags the item and everything below it. Tagging never goes upwards: Jellyfin
   treats a parent's tags as belonging to all of its children, so tagging the series of a shared
   season would hand over every other season.
3. **Redeem.** The link mints a throwaway guest account, applies the policy, and signs the visitor
   in with a server-side session. They land on the title.
4. **Tear down.** Expiry or revocation disables and deletes the guest account, deletes its device
   rows, and strips the tag from the whole tree. A scheduled task and a startup pass catch anything
   that was missed while the server was off.

## Design decisions

| Decision | Reason |
|---|---|
| Only the token HMAC hash is stored | The raw token is returned once and never enters durable storage. Lookups hash the presented token and compare with `FixedTimeEquals`. |
| Public links use `/j/<code>` | The 128-bit capability remains unguessable while keeping links short enough to share comfortably. Watch-party room and media routing are stored in the record instead of exposed as query parameters. |
| The HMAC key is a per-server file, mode 0600 | A stolen `sharelinks.json` yields no usable token. The key is generated on first use. |
| Tags propagate down, never up | A bug fixed in 1.0.3: a shared season tagged its parent series, and Jellyfin's tag inheritance then exposed every other season of that series. |
| Guest accounts use a dedicated authentication provider | It refuses every interactive sign-in, so the login page cannot reach a guest account with any password. If the plugin is disabled, the provider id stops resolving and Jellyfin falls back to its own invalid provider, which also refuses. The design fails closed. |
| The password is generated per redemption and thrown away | The account still needs one, so it is never reachable with a blank password. The browser only ever receives a session token. |
| Redemption is serialized behind a semaphore | The status check and the status write are not atomic. Two simultaneous requests with the same one-use token would otherwise both mint a session. |
| Each multi-use viewer receives its own device id | Jellyfin logs out any session with the same user and device id, so a shared device id would kick out the previous viewer on every new arrival. |
| The viewer ceiling is checked before any write | Jellyfin enforces the same limit itself, but it throws. A throw lands in the failure path, and the failure path tears the share down on everyone already watching. |
| The plugin guard is structural, not a route list | The rule is "core assembly allowed, plugin assemblies refused", not a curated list. A plugin installed next month is covered on the day it lands. |
| Guest devices are deleted before the user | Jellyfin does not cascade, and a device row whose user is gone makes the whole admin devices page fail, not just one row. |

## What the guest can do

- Watch the shared title, and browse down into it. A shared series opens into its seasons and
  episodes. A shared season opens into its episodes.
- Nothing else. No other item, library, search result, collection or plugin route answers them.
- Playback works normally, with transcoding and remuxing if you allow them.

Going up does not work. A guest who receives one season cannot open the series that contains it.

## Multi-use links

A multi-use link works for everyone you send it to until it expires. Treat the URL itself as the
secret. Within that:

- Every viewer uses the same account, so every viewer sees the same single title. More viewers do
  not mean more content.
- The ceiling, 10 by default, caps how many people **start** watching at the same time. It is not a
  cap on how many people use the link in total. Sessions end, and each redemption issues its own
  token. Revoke the link if you need a hard stop.
- One account means shared playback position and shared watch state. Use single-use links if that
  matters.
- A viewer over the ceiling receives a "try again later" page. Nobody watching is disturbed.

## Managing links

The plugin dashboard page lists every share with its status, title, copyable link, guest name and
expiry. You can revoke any link on the spot, which runs the same teardown as expiry. A cleanup
button removes revoked, expired and failed records from the store.

## Install

Add the repository in **Dashboard → Plugins → Manage repositories**:

```
https://raw.githubusercontent.com/rakkateichou/jellyfin-plugin-sharelinks/main/manifest.json
```

Install **ShareLinks**, then restart Jellyfin. Hard-refresh the web client once for the menu entry
to appear.

## Configuration

| Setting | Effect | Default |
|---|---|---|
| Default expiry | The expiry the create popup offers first | 24 h |
| Maximum expiry | The ceiling a link may be set to | 720 h |
| Public base URL override | Forces the host used to build links, instead of the request host | derived |
| Guest username prefix | Prefix for the temporary accounts | `share-` |
| Allow transcoding / remuxing | Whether guest playback may transcode or remux | on |
| Cleanup interval | How often the background cleanup runs | 60 min |
| Maximum viewers per multi-use link | Concurrent viewers on one multi-use link. 0 means no limit | 10 |
| Single use by default | How the create popup starts | on |
| Guest lockdown | The web-client tidying. Cosmetic | on |
| Block other plugins for guests | Refuses guests on other plugins' API routes, server side | on |
| Plugin access list | Plugins that stay reachable by guests. Start empty | empty |
| Cosmetic hidden selectors | CSS selectors hidden from guests. Enforces nothing | empty |

**On the plugin access list:** some plugins have to answer guests. An intro skipper, for example,
is called by the client during playback. Tick that one plugin. Everything starts unticked, so a new
plugin is covered by default.

**On the cosmetic selectors:** the box hides another plugin's floating button from a guest's view.
It runs in the browser. Anyone who opens devtools sees past it. Use the plugin block above for
access, not this.

## HTTP API

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /ShareLinks/Admin/Create` | Admin | Create a link. Returns the short URL once; optional `partyId` and `mediaId` fields bind watch-party routing to its record |
| `GET /ShareLinks/Admin/List` | Admin | All records with status and expiry |
| `POST /ShareLinks/Admin/Revoke/{id}` | Admin | Revoke and tear down |
| `POST /ShareLinks/Admin/Cleanup` | Admin | Remove revoked, expired and failed records |
| `GET /ShareLinks/Admin/Plugins` | Admin | Installed plugins and their guest access state |
| `GET /ShareLinks/GuestState` | Session | Whether the caller is a guest, and what to lock down |
| `GET /j/{code}` | none | Redeem a short code and return the bootstrap page |
| `GET /ShareLinks/Redeem?t=...` | none | Legacy redemption route kept for existing links |
| `GET /ShareLinks/ClientScript` | none | The injected web-client script |

Records live in `sharelinks/sharelinks.json` under Jellyfin's data folder. The HMAC key lives beside
it in `token-secret.key`.

## Known limits

- **Cast and crew are missing on a shared page.** Jellyfin core bug
  [jellyfin/jellyfin#14926](https://github.com/jellyfin/jellyfin/issues/14926): a tag-restricted user
  loses the Cast & Crew section, because the tag filter is applied to people as well as to media. A
  ShareLinks guest is tag-restricted, so it hits this.
- **The short code travels in the URL path.** It appears in reverse-proxy access logs and in browser
  history, like any bearer-style share link.
- **Redemption is public and has no rate limit.** Codes are 128-bit random, so guessing one is not
  realistic, but the endpoint answers anyone.
- **Episodes added after a share** receive the tag on the next redemption, not the moment they are
  added. A one-use link that was already redeemed is a snapshot of the branch at that time.
- **Records are kept after they expire**, for audit. Remove them with the cleanup button.
- **The `sharelinks-` tag is hidden from non-admins in the web client only.** It stays in the API
  response, because that tag is what confines the guest.
- **The guest session token is a real Jellyfin token.** It can be misused in the ways any Jellyfin
  token can. What it reaches is still one title, for the duration you set.

## Compatibility

Jellyfin **10.11** (`targetAbi 10.11.0.0`), .NET 9. Tested on 10.11.8. The web-client injection
targets the shipped client, in English and in French.

## Workflow

**1. Open the context menu on an item**

<img width="566" height="240" alt="ShareLink in the item menu" src="https://github.com/user-attachments/assets/25cfaa99-eed2-4bc6-a14b-bc00bc629d5e" />

**2. Choose an expiry and the single-use option**

<img width="566" height="521" alt="Expiry picker" src="https://github.com/user-attachments/assets/754f3daa-80ee-4209-9d09-467940140f81" />

**3. Copy the link**

<img width="566" height="313" alt="Generated link" src="https://github.com/user-attachments/assets/d9e581eb-d654-4c73-8730-0b2b19fbbe25" />

## License

Developed by [Franciskid](https://github.com/Franciskid). Licensed under the [GPL-3.0](LICENSE),
like most Jellyfin plugins.
