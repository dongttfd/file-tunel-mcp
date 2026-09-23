# IDEA CAPTURE & DESIGN LAW

Status: LOCKED PROCESS LAW

Every new feature or architectural change follows this order exactly:

IDEA -> CAPTURE IMMEDIATELY -> CONTINUE DISCUSSION -> LIVING DRAFT -> DEEP DIVE BY SUBSYSTEM -> CONTINUOUS DESIGN UPDATES -> INDEPENDENT MULTI-ROUND REVIEW -> TECHNOLOGY/SOLUTION COMPARISON -> ARCHITECTURE FREEZE -> DEPENDENCY/TASK SPLIT -> CODE.

Rules:
- An idea is saved immediately without interrupting the conversation flow.
- Idea capture is not approval to code.
- The living design may change freely until frozen.
- Independent audit findings must be written separately from the design they critique.
- Competing technologies/approaches must be compared before architecture freeze when the choice changes reliability, security, persistence, performance, or UX.
- Frozen architecture changes require a new ADR/review round; code must not silently redesign it.
- Tasks must include dependencies and acceptance evidence before implementation starts.
- No feature code before the architecture and task graph for that feature are frozen.
