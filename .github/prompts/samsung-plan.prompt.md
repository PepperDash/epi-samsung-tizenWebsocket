---
description: "Generate a phased implementation plan for Samsung QNxx-QN990FFXZA plugin with roadmap, architecture, and backlog."
argument-hint: "Enter planning scope: device variants, feature scope changes, timeline constraints, or risk concerns."
---

# Samsung QN990 Plugin Plan

You are invoking the **Samsung QN990 Plugin Planner** agent for this task.

## Plan Template

Use the planner to produce:

1. **Objective** - What we're building, v1 scope boundaries, and success criteria
2. **Assumptions** - Samsung API constraints, Essentials patterns, hardware discovery model
3. **Phased Plan**
   - Phase 1: WebSocket protocol integration and auth discovery
   - Phase 2: Device model abstraction and feedback polling
   - Phase 3: Control commands (power, source, volume, mute)
   - Phase 4: LAN discovery and multi-device orchestration
   - Phase 5: Testing hardening and production packaging
4. **Risks and Mitigations** - Auth/reconnection, firmware variance, discovery timeouts
5. **Open Questions** - Firmware differences, WoL behavior, token persistence strategy
6. **Implementation Backlog** - Story slices with acceptance criteria
7. **Next Actions** - Numbered steps to start execution

## Input Guidance

Tailor the plan by providing:
- Device variants or model range to support
- Feature scope overrides (e.g., "add app launching to v1")
- Timeline or milestone targets
- Team constraints or risk priorities
