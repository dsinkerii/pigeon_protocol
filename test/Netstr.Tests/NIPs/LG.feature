Feature: Libregram relay extensions
	Libregram relays expose relay-specific commands over the existing websocket.

Background:
	Given a relay is running
	And Alice is connected to relay
	| PublicKey                                                        | PrivateKey                                                      |
	| 5758137ec7f38f3d6c3ef103e28cd9312652285dab3497fe5e5f6c5c0ef45e75 | nsec12y4pgafw6kpcqjtfyrdyxtcupnddj5kdft768kdl55wzq50ervpqauqnw4 |

Scenario: Relay advertises Libregram capabilities
	When Alice sends a Libregram request req1 lg.capabilities
	Then Alice receives a Libregram OK reply
	| Id   | IsLibregramRelay | Command         |
	| req1 | true             | lg.capabilities |

Scenario: Unknown Libregram command is rejected
	When Alice sends a Libregram request req2 lg.nope
	Then Alice receives a Libregram error reply
	| Id   | Message                                      |
	| req2 | unsupported: unknown libregram command lg.nope |
