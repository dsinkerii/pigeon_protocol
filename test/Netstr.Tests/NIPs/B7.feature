Feature: NIP-B7
	Blossom media support.
	The relay stores kind:10063 (User Server List) replaceable events
	and provides HTTP endpoints for blob storage (BUD-01, BUD-02, BUD-11, BUD-12).

Background: 
	Given a relay is running
	And Alice is connected to relay
	| PublicKey                                                        | PrivateKey                                                       |
	| 5758137ec7f38f3d6c3ef103e28cd9312652285dab3497fe5e5f6c5c0ef45e75 | 512a14752ed58380496920da432f1c0cdad952cd4afda3d9bfa51c2051f91b02 |
	And Bob is connected to relay
	| PublicKey                                                        | PrivateKey                                                       |
	| 5bc683a5d12133a96ac5502c15fe1c2287986cff7baf6283600360e6bb01f627 | 3551fc7617f76632e4542992c0bc01fecb224de639c4b6a1e0956946e8bb8a29 |

Scenario: User publishes a kind:10063 server list event
	Alice publishes a kind:10063 event with server tags.
	The relay should store it as a replaceable event and return it on subscription.
	When Alice publishes an event
	| Id                                                               | Content | Kind  | CreatedAt  | Signature | Tags                                                                       |
	| 5d4d1109236e402555436c91019b599da5841542d3f430ed2b8b06f64c46130f |         | 10063 | 1780615585 |           | [["server","https://cdn.example.com"],["server","https://blossom.self.hosted"]] |
	And Bob sends a subscription request sub1
	| Kinds |
	| 10063 |
	Then Bob receives messages
	| Type  | Id   | EventId                                                              |
	| EVENT | sub1 | 5d4d1109236e402555436c91019b599da5841542d3f430ed2b8b06f64c46130f |
	| EOSE  | sub1 |                                                                      |

Scenario: Kind:10063 is replaceable - newer event replaces older one
	Alice publishes a kind:10063 event, then publishes a newer one.
	Only the newer event should be returned on subscription.
	When Alice publishes an event
	| Id                                                               | Content | Kind  | CreatedAt  | Signature | Tags                                                         |
	| 6a7cedcd64a2ec1761c1ddfec3bd5fffd77acaf730db805ffaa88515367cbe6e |         | 10063 | 1780615585 |           | [["server","https://old.example.com"]]                        |
	And Alice publishes an event
	| Id                                                               | Content | Kind  | CreatedAt  | Signature | Tags                                                          |
	| 2a48099ff0c16e9a9c2d40ee039611e9a38e23f5c7fe9f4dd0b6e9182423a2ef |         | 10063 | 1780615586 |           | [["server","https://new.example.com"],["server","https://cdn.blossom.cloud"]] |
	And Bob sends a subscription request sub1
	| Kinds |
	| 10063 |
	Then Bob receives messages
	| Type  | Id   | EventId                                                              |
	| EVENT | sub1 | 2a48099ff0c16e9a9c2d40ee039611e9a38e23f5c7fe9f4dd0b6e9182423a2ef |
	| EOSE  | sub1 |                                                                      |

Scenario: Different users can have different server lists
	Alice and Bob each publish their own kind:10063 events.
	Filtering by author should return only that user's server list.
	When Alice publishes an event
	| Id                                                               | Content | Kind  | CreatedAt  | Signature | Tags                                            |
	| 310e9b4611b2b3e90d120775282f1cc3e29bf5205469744079138db7f388f9f0 |         | 10063 | 1780615585 |           | [["server","https://alice.blossom.com"]]         |
	And Bob sends a subscription request sub1
	| Authors                                                          |
	| 5758137ec7f38f3d6c3ef103e28cd9312652285dab3497fe5e5f6c5c0ef45e75 |
	Then Bob receives messages
	| Type  | Id   | EventId                                                              |
	| EVENT | sub1 | 310e9b4611b2b3e90d120775282f1cc3e29bf5205469744079138db7f388f9f0 |
	| EOSE  | sub1 |                                                                      |
