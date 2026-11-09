# Lantern.SourceQuery

Source A2S query responder on UDP gameplay port +2 (7779 with the default 7777), so server lists, monitoring
tools and bots can read the server's name, player count, players and rules. It answers `A2S_INFO`,
`A2S_PLAYER` and `A2S_RULES` with the standard challenge, and stays silent while the game is not running.
Challenges expire after 30 seconds.

The wire format is the same as Beacon's `Beacon.SourceQuery`, with Grounded 2's Steam AppID (2661300).
