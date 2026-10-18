# Laplace architecture and operations

Open **Explore → Architecture & operations** in the Laplace application (`/explore/architecture`). This is part of the existing React workspace, using its routing, navigation, controls, error handling and expandable panels. The standalone HTML application has been removed.

The workspace contains the whole-system and domain UML, operation contracts, implementation traces, registration inventory, specification conflicts, source snapshots and publication figures. A numbered outline describes specification order only; it is not an implementation flow. Source traces state the reviewed boundary and unresolved nested behavior. Analytic figures are distinguished from measured results.

The application supports shareable diagram and operation addresses, search by evidence kind, readable original-size diagrams, SVG/PNG/PlantUML downloads, related-flow navigation, exact operation inputs and outputs, source hash inspection and the complete registration inventory. Source snapshots with a different hash from the diagram's reviewed evidence are explicitly marked.

## Sources and build

PlantUML sources, SVG/PNG exports and evidence JSON remain here as repository artifacts. The web application's `gen:architecture` task packages them as static resources for both development and production. It checks every diagram's exports, references and available reviewed-source hashes before packaging. The web build invokes it automatically; no separate documentation server or runtime source checkout is required.

`python rebuild.py` regenerates reviewed diagrams and data, renders exports, validates source references, then packages the application resources. Changed reviewed source stops the build. After reviewing and correcting every affected diagram, `--accept-reviewed-source Repository/path` accepts that particular source revision.

`npm run test:architecture` from `web` exercises the integrated application route, navigation, deep links, operation comparisons, source inspection, downloads, error recovery, responsive layout and accessibility. Build with `npm run build`; `LAPLACE_ARCHITECTURE_PRODUCTION=1 npm run test:architecture` runs those checks against the production output.

`atlas_paths.py` resolves sibling repositories and the existing machine declaration's `LAPLACE_WORK`. Java/PlantUML and research dependencies live in that configured work directory. The generated catalog and source-body snapshots retain their recorded revisions; extraction does not establish semantic review or runtime acceptance. Remaining review boundaries are recorded in `manifest.json`.
