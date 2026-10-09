# Wavetronix message classification

Reference: Wavetronix SmartSensor Advance Data Protocol, WX-501-0072, December
2019, pages 4-8. The vendor PDF is not redistributed in this repository.

## Actuation

`X1` followed by exactly four ASCII hexadecimal digits and `~ CR CR` is an
actuation response. It may have a six-character multi-drop prefix: `Z0` and
four decimal digits. Example: `Z01478X10001~ CR CR` identifies multi-drop sensor
1478 with alert 1 active; `Z01396X10000~ CR CR` reports no active alerts for 1396.
These messages do not contain vehicle speed and are never archived as speed
records. They increment `Actuation`; malformed responses remain `Rejected`.

## Trigger speed

The documented basic message is seven bytes: `XS`, one binary MPH byte, one
binary KPH byte, and `~ CR CR`. Example `58 53 32 50 7E 0D 0D` is 50 MPH / 80 KPH.
Multi-drop basic messages carry the same six-character prefix described above.
The multi-drop ID is not the ATSPM detector identifier.

Existing six-digit-tagged compact and prefixed speed formats continue to work.
For an untagged message, explicitly map its exact source IP and UDP port to a
six-digit ATSPM detector ID in the existing `SpeedListenerConfiguration` section:

```json
"UntaggedSpeedDetectorMappings": {
  "192.0.2.10:2102": "502620",
  "192.0.2.10:2103": "502622"
}
```

Addresses above are examples, not discovered sensor settings. Confirm the actual
source endpoint and channel with the sensor administrator. Mapping uses the UDP
source port, not the listener destination port. Use canonical endpoint notation
(`[2001:db8::1]:2102` for IPv6). There is no IP-only fallback. If multiple sensors
or channels share the same source endpoint, configure unique endpoints or tagged
output; this setting cannot safely distinguish them. Restart after editing.

The mapped detector goes through the same current-location-version lookup as
normal tagged events: the first four digits identify the location and its first
speed device receives the event. Receipt time and configured agency time zone
apply. Configured endpoint mappings never override tagged detector identifiers
and never convert actuation or unsupported binary data into speed events.

Without a mapping, a recognized basic speed message increments `UnmappedSpeed`,
logs a Debug diagnostic, and produces an aggregate Warning once per summary
interval when additional unmapped messages arrive. No speed event is fabricated.

## Unsupported binary data

The reference says Advance Extended Range uses `Z4` for actuation, but explicitly
excludes the Z4 specification. Captured binary prefixes `5A34` spell `Z4`, but
that does not establish field layout, checksums, bundling or reassembly rules.
They remain `Rejected` and keep their Debug payload previews until the applicable
vendor specification is available. `XT` track-file messages are also unsupported;
repeated tracking observations must not be treated as individual speed triggers.

## Counters

`Received` counts UDP datagrams. `Parsed` counts queued speed events; `Actuation`,
`UnmappedSpeed`, and `Rejected` count classified messages. A datagram may contain
multiple messages. Classification preserves subsequent terminated speed messages.
Cross-datagram fragments are not reassembled.
