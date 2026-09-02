# FIDE live contract

FIDE is an external provider identity surface, not a name-equality heuristic.

Required behavior:

- a 4-12 digit FIDE ID is an exact lookup and must fetch that exact profile;
- FIDE search/table profile links may be relative (`profile/<id>`), root-relative, or absolute;
- current FIDE profile HTML may carry U+FEFF before the player name;
- top-list parsing must not depend on one historical CSS class spelling when the provider supplies stable table labels/content;
- every emitted FIDE provider ID is numeric;
- the live test calls the official provider for exact ID `2016192`, name `Hikaru`, and the open top list; zero candidates is a failed contract, not success.

The Lab/player identity work remains subject to the separate rule that explicit cross-provider association must be witnessed and auditable; matching names alone are not identity.
