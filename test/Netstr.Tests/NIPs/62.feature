Feature: NIP-62 (Two-Stage Vanish)
	Nostr-native way to request a complete reset of a key's fingerprint on the web.
	Libregram extends NIP-62 with a two-stage process: cancellable "will to vanish" 
	followed by irreversible execution at ban_at.

Background: 
	Given a relay is running
	And Alice is connected to relay
	| PublicKey                                                        | PrivateKey                                                       |
	| 5758137ec7f38f3d6c3ef103e28cd9312652285dab3497fe5e5f6c5c0ef45e75 | 512a14752ed58380496920da432f1c0cdad952cd4afda3d9bfa51c2051f91b02 |
	And Bob is connected to relay
	| PublicKey                                                        | PrivateKey                                                       |
	| 5bc683a5d12133a96ac5502c15fe1c2287986cff7baf6283600360e6bb01f627 | 3551fc7617f76632e4542992c0bc01fecb224de639c4b6a1e0956946e8bb8a29 |
	And Charlie is connected to relay
	| PublicKey                                                        | PrivateKey                                                       |
	| fe8d7a5726ea97ce6140f9fb06b1fe7d3259bcbf8de42c2a5d2ec9f8f0e2f614 | f77f81a6a223eb15f81fee569161a4f729401a9cbc31bb69fef6a949b9d3c23a |

Scenario: Request to Vanish requires ban_at tag
	Kind-62 without ban_at is rejected.
	When Alice publishes events
	| Id                                                               | Content        | Kind | Tags                     | CreatedAt  |
	| ff1092c354d94060a185f8b5e4349499079872babe27b882fd4632efcdd001c2 | Hello          | 1    |                          | 1728905459 |
	| 9766e0efe45ecd90c508e66a8dd3eee3a7f16be33af87aded9fc779f40237d0e | I'm outta here | 62   | [["relay","ALL_RELAYS"]] | 1728905470 |
	Then Alice receives messages
	| Type | EventId                                                          | Success |
	| OK   | ff1092c354d94060a185f8b5e4349499079872babe27b882fd4632efcdd001c2 | true    |
	| OK   | 9766e0efe45ecd90c508e66a8dd3eee3a7f16be33af87aded9fc779f40237d0e | false   |

Scenario: Request to Vanish with ban_at creates pending
	Kind-62 with valid future ban_at is accepted. Events NOT deleted.
	When Alice publishes events
	| Id                                                               | Content     | Kind | Tags                                                          | CreatedAt  |
	| ff1092c354d94060a185f8b5e4349499079872babe27b882fd4632efcdd001c2 | Hello       | 1    |                                                               | 1728905459 |
	| f45c291b8c4e3a164e68932f251e50b4182f4dfe2eca76081a7ca2d759568dfd | Hello Later | 1    |                                                               | 1728905480 |
	| 272256aaa438e89222e5cc58bda673dba4597ac40eea2e01dee7b6efd753cde5 | vanish will | 62   | [["relay","ALL_RELAYS"],["ban_at","2528905470"]]              | 1728905470 |
	And Charlie sends a subscription request abcd
	| Authors                                                                                                                           |
	| 5758137ec7f38f3d6c3ef103e28cd9312652285dab3497fe5e5f6c5c0ef45e75,5bc683a5d12133a96ac5502c15fe1c2287986cff7baf6283600360e6bb01f627 |
	Then Charlie receives messages
	| Type  | Id   | EventId                                                          |
	| EVENT | abcd | f45c291b8c4e3a164e68932f251e50b4182f4dfe2eca76081a7ca2d759568dfd |
	| EVENT | abcd | 272256aaa438e89222e5cc58bda673dba4597ac40eea2e01dee7b6efd753cde5 |
	| EVENT | abcd | ff1092c354d94060a185f8b5e4349499079872babe27b882fd4632efcdd001c2 |
	| EOSE  | abcd |                                                                  |

Scenario: Duplicate pending vanish rejected
	Second kind-62 while one pending is rejected.
	When Alice publishes events
	| Id                                                               | Content      | Kind | Tags                                                          | CreatedAt  |
	| 272256aaa438e89222e5cc58bda673dba4597ac40eea2e01dee7b6efd753cde5 | vanish will  | 62   | [["relay","ALL_RELAYS"],["ban_at","2528905470"]]              | 1728905470 |
	| dbb351bfcb8abac6e077cadf220a2dfece76376d4e2eff19c31860a633779021 | vanish later | 62   | [["relay","ALL_RELAYS"],["ban_at","2628905490"]]              | 1728905490 |
	Then Alice receives messages
	| Type | EventId                                                          | Success |
	| OK   | 272256aaa438e89222e5cc58bda673dba4597ac40eea2e01dee7b6efd753cde5 | true    |
	| OK   | dbb351bfcb8abac6e077cadf220a2dfece76376d4e2eff19c31860a633779021 | false   |

Scenario: Cancel pending vanish within window
	Action=cancel with reference to original event cancels pending vanish.
	When Alice publishes events
	| Id                                                               | Content      | Kind | Tags                                                                              | CreatedAt  |
	| 5b1d0f938a672ae13a70860e43baa53ca78ef1ce227546486d753f14e2404170 | vanish will  | 62   | [["relay","ALL_RELAYS"],["ban_at","2528905470"],["cancel_before","1928905470"]]   | 1728905470 |
	| d9149958a88c2fa31811ab265fd4308b06f9f4cf2e3d7225fdc130e37c5dcef0 | cancel vanish| 62   | [["relay","ALL_RELAYS"],["action","cancel"],["e","5b1d0f938a672ae13a70860e43baa53ca78ef1ce227546486d753f14e2404170"]] | 1728905480 |
	Then Alice receives messages
	| Type | EventId                                                          | Success |
	| OK   | 5b1d0f938a672ae13a70860e43baa53ca78ef1ce227546486d753f14e2404170 | true    |
	| OK   | d9149958a88c2fa31811ab265fd4308b06f9f4cf2e3d7225fdc130e37c5dcef0 | true    |

Scenario: Cancel non-existent vanish rejected
	Cancel without matching pending vanish is rejected.
	When Alice publishes events
	| Id                                                               | Content       | Kind | Tags                                                                              | CreatedAt  |
	| d690fb40e920e2933dd727c62bbbc137f15a84044eecfca3d96fe3cf2c78ca53 | cancel phantom| 62   | [["relay","ALL_RELAYS"],["action","cancel"],["e","NONEXISTENT"]]                  | 1728905480 |
	Then Alice receives messages
	| Type | EventId                                                          | Success |
	| OK   | d690fb40e920e2933dd727c62bbbc137f15a84044eecfca3d96fe3cf2c78ca53 | false   |

Scenario: Kind-5 deletion of kind-62 rejected
	Delete request against a request to vanish has no effect.
	When Alice publishes events
	| Id                                                               | Content        | Kind | Tags                                                                                 | CreatedAt  |
	| 272256aaa438e89222e5cc58bda673dba4597ac40eea2e01dee7b6efd753cde5 | vanish will    | 62   | [["relay","ALL_RELAYS"],["ban_at","2528905470"]]                                     | 1728905470 |
	| d0918ee16d6e7faf79bfbaec42cb6771a9c21fac630d41492745d8072a9b05e7 |                | 5    | [["e", "272256aaa438e89222e5cc58bda673dba4597ac40eea2e01dee7b6efd753cde5"]]          | 1728905471 |
	Then Alice receives messages
	| Type | EventId                                                          | Success |
	| OK   | 272256aaa438e89222e5cc58bda673dba4597ac40eea2e01dee7b6efd753cde5 | true    |
	| OK   | d0918ee16d6e7faf79bfbaec42cb6771a9c21fac630d41492745d8072a9b05e7 | false   |

Scenario: Relay tag must match
	Missing/incorrect relay tag rejected. Correct one accepted.
	When Alice publishes events
	| Id                                                               | Content        | Kind | Tags                                                   | CreatedAt  |
	| 760dd93c57151698628a28866366971d02e6a075c54bae7e84fd7740a90c0a2c | vanish no tag  | 62   |                                                        | 1728905470 |
	| 4d7435a84d44fea8c41fa8ccd122502a44c2ac54c5d3bfeb512676697eca6168 | vanish bad tag | 62   | [["relay","blabla"],["ban_at","2528905470"]]           | 1728905470 |
	| de57395f11aa96d286dd679c140fb04a21a0ad4eaae1465936c51c7be00fe1d9 | vanish correct | 62   | [["relay","ws://localhost/"],["ban_at","2528905470"]]  | 1728905470 |
	Then Alice receives messages
	| Type | EventId                                                          | Success |
	| OK   | 760dd93c57151698628a28866366971d02e6a075c54bae7e84fd7740a90c0a2c | false   |
	| OK   | 4d7435a84d44fea8c41fa8ccd122502a44c2ac54c5d3bfeb512676697eca6168 | false   |
	| OK   | de57395f11aa96d286dd679c140fb04a21a0ad4eaae1465936c51c7be00fe1d9 | true    |