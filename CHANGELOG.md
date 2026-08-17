# Changelog

Modgud is pre-1.0. While that's true, the [Roadmap](./docs/roadmap.md)
is the source of truth for what's shipped and what's coming —
revising it every time something lands keeps one canonical view
rather than two that drift.

The versioned changelog starts at **v1.0.0**. Once we tag v1.0, this
file follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/)
and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Feature-flagged Positions and shared terminals (MG-FT-FLEX), including
  multi-position terminal enrollment, configurable activation proofs and
  device bindings, realm security floors, staffing/refresh/step-up lifecycle,
  activation-token administration, and the matching admin UI and consumer
  contract. `PositionTerminals` remains off by default and is enabled with
  `AppSettings__Features__PositionTerminals=true`.

See the [Roadmap](./docs/roadmap.md) for the full pre-1.0 product snapshot and
what remains intentionally out of scope. Day-to-day history lives in `git log`.
