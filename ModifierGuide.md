# Writing Modifier Scripts (.gh)

A modifier is a standard Grasshopper definition with a specific layout convention. The plugin discovers inputs, outputs, and geometry piping by looking for named groups and parameter nicknames.

## Geometry Piping (GeomIn / GeomOut)

**GeomIn** — Place a standalone `Geometry` param anywhere on the canvas (NOT inside the Inputs group). Set its NickName to `GeomIn`. Leave it **unwired** on its left side — the engine injects geometry into it at runtime.

**GeomOut** — Place a `Geometry` param inside the **Outputs** group (see below). The engine reads whatever geometry lands on this param and passes it to the next modifier.

Geometry flows as a **list**. If a modifier outputs 6 surfaces, the next modifier's GeomIn receives all 6 as a standard GH data list.

## Groups

Create two GH groups with exact NickNames:

| Group | Purpose |
|-------|---------|
| **Inputs** | Contains all user-facing input parameters (sliders, numbers, points, booleans, colors, geometry, strings) |
| **Outputs** | Contains all output parameters — including GeomOut geometry params and any published value outputs (numbers, strings, etc.) |

## Inputs Group

Place **unwired** parameters or sliders inside the Inputs group. Supported types:

- **Number Slider** — appears as a slider in the panel (min/max/decimals preserved)
- **Number** / **Integer** — numeric input
- **Point** — point input (formatted `x,y,z`)
- **Boolean** — toggle
- **Colour** — color picker (`#RRGGBB` or `r,g,b`)
- **String** — text input
- **Geometry** — geometry input (auto-receives the piped scene geometry when left blank)

Every input param must be **unwired on its input side** (no left-side connections from other components). Wire its right side into your logic as normal.

The param's **NickName** becomes its label in the modifier panel.

## Outputs Group

Place params inside the Outputs group to publish values downstream. Other modifiers can **link** to these outputs.

A `Geometry` param in the Outputs group automatically acts as a geometry output (equivalent to the legacy GeomOut nickname).

## Minimal Example

```
┌─────────────────────────────────────────────────┐
│                                                 │
│   [Geometry: "GeomIn"]  ──►  [your logic]       │
│         (standalone, unwired left side)          │
│                                                 │
│   ┌── Inputs ──────────────┐                    │
│   │  [Number Slider: "Amt"]│──►  [your logic]   │
│   │  [Boolean: "Flip"]     │──►                  │
│   └────────────────────────┘                    │
│                                                 │
│                        [your logic]  ──►        │
│   ┌── Outputs ─────────────┐                    │
│   │  [Geometry: "GeomOut"] │                     │
│   │  [Number: "Area"]      │                     │
│   └────────────────────────┘                    │
│                                                 │
└─────────────────────────────────────────────────┘
```

## Rules

1. **GeomIn must be standalone** — outside both groups, no input wires
2. **All Inputs group params must be unwired** on their input side
3. **Group NickNames are case-insensitive** — `Inputs`, `inputs`, `INPUTS` all work
4. **GeomIn/GeomOut nicknames are case-insensitive** — `GeomIn`, `GeoIn` both work; `GeomOut`, `GeoOut` both work
5. **Lists pass through** — a modifier can output more items than it receives (e.g., explode) or fewer (e.g., join). The next modifier gets whatever the previous one outputs.
6. **Geometry in the Inputs group** auto-fills with the piped geometry when the user doesn't set an explicit value — useful for modifiers that need geometry on a named input rather than through the legacy GeomIn
