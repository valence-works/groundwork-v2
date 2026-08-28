#!/usr/bin/env python3
"""Reconcile the Groundwork v2 roadmap issues into an organization project.

The module deliberately uses only Python's standard library.  The pure helpers
(``is_roadmap_issue``, ``closing_issue_numbers``, ``derive_status``, and
``build_sync_plan``) are also used by the unit tests that run in ordinary PR
CI, where no project token is available.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from dataclasses import dataclass
from typing import Any, Callable, Iterable, Mapping, Optional, Sequence
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen


DEFAULT_REPOSITORY = "valence-works/groundwork-v2"
DEFAULT_ORGANIZATION = "valence-works"
DEFAULT_PROJECT_NUMBER = 6
ROADMAP_LABEL = "roadmap-2.0"
TODO = "Todo"
IN_PROGRESS = "In Progress"
DONE = "Done"

# GitHub recognizes these keywords when they precede an issue reference in a
# pull-request body (and in commit messages).  The title is intentionally not
# included: a closing-looking title does not close an issue when the PR merges.
_CLOSING_REFERENCE = re.compile(
    r"\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s*:?[ \t]+"
    r"(?:(?P<owner>[A-Za-z0-9_.-]+)/(?P<repo>[A-Za-z0-9_.-]+))?#(?P<number>[0-9]+)",
    re.IGNORECASE,
)
_CLOSING_URL = re.compile(
    r"\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s*:?[ \t]+"
    r"https?://github\.com/(?P<owner>[A-Za-z0-9_.-]+)/(?P<repo>[A-Za-z0-9_.-]+)/issues/(?P<number>[0-9]+)",
    re.IGNORECASE,
)


def _repository_name(value: Any) -> Optional[str]:
    """Return a repository's full name from common REST/GraphQL shapes."""

    if isinstance(value, str):
        return value
    if isinstance(value, Mapping):
        for key in ("full_name", "nameWithOwner"):
            name = value.get(key)
            if isinstance(name, str):
                return name
    return None


def _issue_repository(issue: Mapping[str, Any]) -> Optional[str]:
    return _repository_name(issue.get("repository")) or _repository_name(issue.get("repo"))


def _labels(issue: Mapping[str, Any]) -> Iterable[str]:
    for label in issue.get("labels") or ():
        if isinstance(label, str):
            yield label
        elif isinstance(label, Mapping) and isinstance(label.get("name"), str):
            yield label["name"]


def is_roadmap_issue(issue: Mapping[str, Any], repository: str = DEFAULT_REPOSITORY) -> bool:
    """Whether an API issue belongs to this board's roadmap projection."""

    # The repository-scoped REST endpoint normally omits this property.  When
    # present (or in tests), requiring it prevents a similarly numbered issue
    # from another repository from entering the project.
    issue_repository = _issue_repository(issue)
    return (issue_repository is None or issue_repository == repository) and any(
        label.casefold() == ROADMAP_LABEL.casefold() for label in _labels(issue)
    )


def roadmap_issues(
    issues: Iterable[Mapping[str, Any]], repository: str = DEFAULT_REPOSITORY
) -> tuple[Mapping[str, Any], ...]:
    """Return roadmap issues in deterministic issue-number order."""

    selected = [
        issue
        for issue in issues
        if "pull_request" not in issue and is_roadmap_issue(issue, repository)
    ]
    return tuple(sorted(selected, key=lambda issue: int(issue.get("number", 0))))


def _pull_request_repository(pull_request: Mapping[str, Any]) -> Optional[str]:
    base = pull_request.get("base")
    if isinstance(base, Mapping):
        repository = _repository_name(base.get("repo"))
        if repository:
            return repository
    return _repository_name(pull_request.get("repository"))


def _closing_references(text: str, repository: str) -> set[int]:
    numbers: set[int] = set()
    for pattern in (_CLOSING_REFERENCE, _CLOSING_URL):
        for match in pattern.finditer(text):
            owner = match.group("owner")
            repo = match.group("repo")
            if owner is not None and repo is not None and f"{owner}/{repo}".casefold() != repository.casefold():
                continue
            numbers.add(int(match.group("number")))
    return numbers


def closing_issue_numbers(
    pull_requests: Iterable[Mapping[str, Any]],
    repository: str = DEFAULT_REPOSITORY,
    default_branch: Optional[str] = None,
) -> frozenset[int]:
    """Return issue numbers explicitly closed by open PRs in ``repository``.

    GitHub recognizes closing keywords in a pull-request body and in commit
    messages that will be merged.  The API query is scoped to open pull
    requests, but checking ``state`` here keeps the pure function correct for
    fixtures and callers that pass a mixed collection.  Cross-repository
    references are ignored.  When ``default_branch`` is supplied, only PRs
    targeting that branch can contribute closing references.
    """

    numbers: set[int] = set()
    for pull_request in pull_requests:
        if str(pull_request.get("state", "open")).casefold() != "open":
            continue
        pull_request_repository = _pull_request_repository(pull_request)
        if pull_request_repository is not None and pull_request_repository.casefold() != repository.casefold():
            continue
        if default_branch is not None:
            base = pull_request.get("base")
            base_ref = base.get("ref") if isinstance(base, Mapping) else None
            if not isinstance(base_ref, str) or base_ref != default_branch:
                continue
        body = pull_request.get("body")
        if isinstance(body, str):
            numbers.update(_closing_references(body, repository))
        for commit in pull_request.get("commits") or ():
            if not isinstance(commit, Mapping):
                continue
            commit_data = commit.get("commit")
            message = (
                commit_data.get("message")
                if isinstance(commit_data, Mapping)
                else commit.get("message")
            )
            if isinstance(message, str):
                numbers.update(_closing_references(message, repository))
    return frozenset(numbers)


def derive_status(
    issue: Mapping[str, Any], open_closing_issue_numbers: Iterable[int]
) -> str:
    """Derive the exact project status for one issue."""

    if str(issue.get("state", "open")).casefold() == "closed":
        return DONE
    if issue.get("assignees") or int(issue.get("number", 0)) in set(open_closing_issue_numbers):
        return IN_PROGRESS
    return TODO


@dataclass(frozen=True)
class SyncAction:
    """One non-destructive project mutation in a reconciliation plan."""

    kind: str
    issue_number: int
    status: str
    item_id: Optional[str] = None
    content_id: Optional[str] = None


def _item_issue_key(item: Mapping[str, Any], repository: str) -> Optional[tuple[str, int]]:
    content = item.get("content")
    if not isinstance(content, Mapping) or content.get("__typename") != "Issue":
        return None
    content_repository = _repository_name(content.get("repository"))
    if content_repository != repository:
        return None
    try:
        return repository, int(content["number"])
    except (KeyError, TypeError, ValueError):
        return None


def _current_status(item: Mapping[str, Any]) -> Optional[str]:
    direct_value = item.get("statusValue")
    if isinstance(direct_value, Mapping):
        name = direct_value.get("name")
        if isinstance(name, str):
            return name
    values = item.get("fieldValues")
    if not isinstance(values, Mapping):
        return None
    nodes = values.get("nodes")
    if not isinstance(nodes, Sequence) or isinstance(nodes, (str, bytes)):
        return None
    for value in nodes:
        if isinstance(value, Mapping) and value.get("field_name") == "Status":
            name = value.get("name")
            return name if isinstance(name, str) else None
        if isinstance(value, Mapping):
            field = value.get("field")
            if isinstance(field, Mapping) and field.get("name") == "Status":
                name = value.get("name")
                return name if isinstance(name, str) else None
    return None


def build_sync_plan(
    issues: Iterable[Mapping[str, Any]],
    pull_requests: Iterable[Mapping[str, Any]],
    project_items: Iterable[Mapping[str, Any]],
    repository: str = DEFAULT_REPOSITORY,
    default_branch: Optional[str] = None,
) -> tuple[SyncAction, ...]:
    """Plan additions and status updates without mutating GitHub."""

    selected = roadmap_issues(issues, repository)
    closing_numbers = closing_issue_numbers(pull_requests, repository, default_branch)
    existing: dict[tuple[str, int], Mapping[str, Any]] = {}
    for item in project_items:
        key = _item_issue_key(item, repository)
        if key is not None:
            existing[key] = item

    actions: list[SyncAction] = []
    for issue in selected:
        number = int(issue["number"])
        desired = derive_status(issue, closing_numbers)
        item = existing.get((repository, number))
        if item is None:
            content_id = issue.get("node_id") or issue.get("id")
            if not isinstance(content_id, str):
                raise ValueError(f"roadmap issue #{number} has no node_id/content id")
            actions.append(
                SyncAction("add", number, desired, content_id=content_id)
            )
            continue
        current = _current_status(item)
        item_id = item.get("id")
        if current != desired and isinstance(item_id, str):
            actions.append(SyncAction("status", number, desired, item_id=item_id))
    return tuple(actions)


class GitHubError(RuntimeError):
    """An API call failed in a way that should fail the workflow."""

    def __init__(self, message: str, *, status: Optional[int] = None):
        super().__init__(message)
        self.status = status


class GitHubClient:
    """Small GitHub REST/GraphQL client with no third-party dependencies."""

    def __init__(
        self,
        token: str,
        api_url: str = "https://api.github.com",
        graphql_url: Optional[str] = None,
        sleep_fn: Callable[[float], None] = time.sleep,
    ):
        if not token:
            raise ValueError("PROJECT_TOKEN is required")
        self._api_url = api_url.rstrip("/")
        self._graphql_url = graphql_url or self._api_url + "/graphql"
        self._sleep = sleep_fn
        self._headers = {
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {token}",
            "User-Agent": "groundwork-v2-project-sync",
            "X-GitHub-Api-Version": "2022-11-28",
        }

    def _request(self, method: str, url: str, body: Optional[Mapping[str, Any]] = None) -> Any:
        encoded = None if body is None else json.dumps(body).encode("utf-8")
        headers = dict(self._headers)
        if body is not None:
            headers["Content-Type"] = "application/json"
        retry_safe = method.upper() in {"GET", "HEAD"}
        if method.upper() == "POST" and isinstance(body, Mapping):
            query = body.get("query")
            retry_safe = isinstance(query, str) and query.lstrip().startswith("query")

        for attempt in range(1, 4):
            request = Request(url, data=encoded, headers=headers, method=method)
            try:
                with urlopen(request, timeout=45) as response:
                    payload = response.read()
            except HTTPError as error:
                details = ""
                try:
                    details = error.read().decode("utf-8", errors="replace")
                except OSError:
                    pass
                if retry_safe and attempt < 3 and self._should_retry_http_error(error, details):
                    self._sleep(self._retry_delay(attempt, error))
                    continue
                raise GitHubError(
                    f"GitHub {method} {url} failed: {error} {details}",
                    status=error.code,
                ) from error
            except (URLError, TimeoutError) as error:
                if retry_safe and attempt < 3:
                    self._sleep(float(2 ** (attempt - 1)))
                    continue
                raise GitHubError(f"GitHub {method} {url} failed: {error}") from error
            break
        if not payload:
            return None
        try:
            return json.loads(payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise GitHubError(f"GitHub {method} {url} returned invalid JSON") from error

    @staticmethod
    def _should_retry_http_error(error: HTTPError, details: str) -> bool:
        status = error.code
        if status == 429 or 500 <= status <= 599:
            return True
        if status != 403:
            return False
        retry_after = error.headers.get("Retry-After") if error.headers else None
        lowered = details.casefold()
        return bool(retry_after) or "secondary rate limit" in lowered or "abuse detection" in lowered

    @staticmethod
    def _retry_delay(attempt: int, error: HTTPError) -> float:
        retry_after = error.headers.get("Retry-After") if error.headers else None
        try:
            if retry_after is not None:
                return min(max(float(retry_after), 0.0), 10.0)
        except ValueError:
            pass
        return float(min(2 ** (attempt - 1), 10))

    def get_pages(self, path: str, **params: Any) -> list[Mapping[str, Any]]:
        """Fetch every REST page, including a final empty page when needed."""

        page = 1
        results: list[Mapping[str, Any]] = []
        while True:
            query = dict(params)
            query.update(page=page, per_page=100)
            url = f"{self._api_url}{path}?{urlencode(query)}"
            payload = self._request("GET", url)
            if not isinstance(payload, list):
                raise GitHubError(f"GitHub {path} returned a non-list payload")
            results.extend(item for item in payload if isinstance(item, Mapping))
            if len(payload) < 100:
                return results
            page += 1

    def list_roadmap_issues(self, repository: str) -> list[Mapping[str, Any]]:
        # The issues endpoint includes PRs; roadmap_issues filters those out
        # through the absence of an issue-only repository shape in fixtures and
        # the explicit pull_request marker in real REST responses below.
        path = f"/repos/{repository}/issues"
        issues = self.get_pages(path, state="all", labels=ROADMAP_LABEL)
        return [issue for issue in issues if "pull_request" not in issue]

    def list_open_pull_requests(self, repository: str) -> list[Mapping[str, Any]]:
        pull_requests = self.get_pages(
            f"/repos/{repository}/pulls", state="open", sort="updated", direction="desc"
        )
        enriched: list[Mapping[str, Any]] = []
        for pull_request in pull_requests:
            number = pull_request.get("number")
            if not isinstance(number, int):
                enriched.append(pull_request)
                continue
            try:
                commits = self.get_pages(f"/repos/{repository}/pulls/{number}/commits")
            except GitHubError as error:
                # A PR can disappear or close between the list and commit
                # requests.  Its closing state is no longer trustworthy, so
                # omit that PR rather than treating it as body-only work.
                if error.status in (404, 410):
                    continue
                raise
            enriched_pull_request = dict(pull_request)
            enriched_pull_request["commits"] = commits
            enriched.append(enriched_pull_request)
        return enriched

    def get_default_branch(self, repository: str) -> str:
        payload = self._request("GET", f"{self._api_url}/repos/{repository}")
        default_branch = payload.get("default_branch") if isinstance(payload, Mapping) else None
        if not isinstance(default_branch, str) or not default_branch:
            raise GitHubError(f"GitHub repository {repository} has no default branch")
        return default_branch

    def get_project_snapshot(self, organization: str, project_number: int) -> Mapping[str, Any]:
        fields_query = """
        query($organization: String!, $number: Int!, $after: String) {
          organization(login: $organization) {
            projectV2(number: $number) {
              id
              fields(first: 100, after: $after) {
                nodes {
                  __typename
                  ... on ProjectV2SingleSelectField {
                    id
                    name
                    options { id name }
                  }
                }
                pageInfo { hasNextPage endCursor }
              }
            }
          }
        }
        """
        items_query = """
        query($organization: String!, $number: Int!, $after: String) {
          organization(login: $organization) {
            projectV2(number: $number) {
              id
              items(first: 100, after: $after) {
                nodes {
                  id
                  content {
                    __typename
                    ... on Issue {
                      id
                      number
                      repository { nameWithOwner }
                    }
                    ... on PullRequest {
                      id
                      number
                      repository { nameWithOwner }
                    }
                  }
                  statusValue: fieldValueByName(name: "Status") {
                    __typename
                    ... on ProjectV2ItemFieldSingleSelectValue {
                      name
                      optionId
                    }
                  }
                }
                pageInfo { hasNextPage endCursor }
              }
            }
          }
        }
        """

        def read_project(payload: Any) -> Mapping[str, Any]:
            if not isinstance(payload, Mapping):
                raise GitHubError("GitHub GraphQL returned a non-object payload")
            errors = payload.get("errors")
            if errors:
                raise GitHubError(f"GitHub GraphQL project query failed: {errors}")
            data = payload.get("data")
            organization_data = data.get("organization") if isinstance(data, Mapping) else None
            project = organization_data.get("projectV2") if isinstance(organization_data, Mapping) else None
            if not isinstance(project, Mapping):
                raise GitHubError(f"organization project #{project_number} was not found")
            return project

        project_id: Optional[str] = None
        fields: list[Mapping[str, Any]] = []
        field_cursor: Optional[str] = None
        while True:
            project = read_project(
                self._request(
                    "POST",
                    self._graphql_url,
                    {
                        "query": fields_query,
                        "variables": {
                            "organization": organization,
                            "number": project_number,
                            "after": field_cursor,
                        },
                    },
                )
            )
            current_project_id = project.get("id")
            if not isinstance(current_project_id, str):
                raise GitHubError("GitHub GraphQL project has no id")
            if project_id is None:
                project_id = current_project_id
            elif current_project_id != project_id:
                raise GitHubError("GitHub GraphQL project id changed while paging fields")
            field_connection = project.get("fields")
            if not isinstance(field_connection, Mapping):
                raise GitHubError("GitHub GraphQL project query returned no fields connection")
            field_nodes = field_connection.get("nodes", [])
            fields.extend(field for field in field_nodes if isinstance(field, Mapping))
            page_info = field_connection.get("pageInfo")
            if not isinstance(page_info, Mapping) or not page_info.get("hasNextPage"):
                break
            field_cursor = page_info.get("endCursor")
            if not isinstance(field_cursor, str):
                raise GitHubError("GitHub GraphQL project fields page has no end cursor")

        items: list[Mapping[str, Any]] = []
        item_cursor: Optional[str] = None
        while True:
            project = read_project(
                self._request(
                    "POST",
                    self._graphql_url,
                    {
                        "query": items_query,
                        "variables": {
                            "organization": organization,
                            "number": project_number,
                            "after": item_cursor,
                        },
                    },
                )
            )
            current_project_id = project.get("id")
            if not isinstance(current_project_id, str):
                raise GitHubError("GitHub GraphQL project has no id")
            if project_id is None:
                project_id = current_project_id
            elif current_project_id != project_id:
                raise GitHubError("GitHub GraphQL project id changed while paging items")
            item_connection = project.get("items")
            if not isinstance(item_connection, Mapping):
                raise GitHubError("GitHub GraphQL project query returned no items connection")
            item_nodes = item_connection.get("nodes", [])
            items.extend(item for item in item_nodes if isinstance(item, Mapping))
            page_info = item_connection.get("pageInfo")
            if not isinstance(page_info, Mapping) or not page_info.get("hasNextPage"):
                break
            item_cursor = page_info.get("endCursor")
            if not isinstance(item_cursor, str):
                raise GitHubError("GitHub GraphQL project page has no end cursor")
        if project_id is None:
            raise GitHubError("GitHub GraphQL project has no id")

        status_field = next(
            (field for field in fields if str(field.get("name", "")).casefold() == "status"),
            None,
        )
        if not isinstance(status_field, Mapping) or not isinstance(status_field.get("id"), str):
            raise GitHubError("project has no single-select Status field")
        options = {
            option.get("name"): option.get("id")
            for option in status_field.get("options", [])
            if isinstance(option, Mapping)
            and isinstance(option.get("name"), str)
            and isinstance(option.get("id"), str)
        }
        missing = [status for status in (TODO, IN_PROGRESS, DONE) if status not in options]
        if missing:
            raise GitHubError(f"project Status field is missing options: {', '.join(missing)}")
        return {
            "id": project_id,
            "status_field_id": status_field["id"],
            "status_options": options,
            "items": items,
        }

    def add_project_item(self, project_id: str, content_id: str) -> str:
        mutation = """
        mutation($projectId: ID!, $contentId: ID!) {
          addProjectV2ItemById(input: {projectId: $projectId, contentId: $contentId}) {
            item { id }
          }
        }
        """
        payload = self._request(
            "POST",
            self._graphql_url,
            {"query": mutation, "variables": {"projectId": project_id, "contentId": content_id}},
        )
        if not isinstance(payload, Mapping) or payload.get("errors"):
            raise GitHubError(f"GitHub GraphQL add-item mutation failed: {payload}")
        item = payload.get("data", {}).get("addProjectV2ItemById", {}).get("item")
        item_id = item.get("id") if isinstance(item, Mapping) else None
        if not isinstance(item_id, str):
            raise GitHubError("GitHub GraphQL add-item mutation returned no item id")
        return item_id

    def update_project_status(self, project_id: str, item_id: str, field_id: str, option_id: str) -> None:
        mutation = """
        mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $optionId: String!) {
          updateProjectV2ItemFieldValue(input: {
            projectId: $projectId
            itemId: $itemId
            fieldId: $fieldId
            value: { singleSelectOptionId: $optionId }
          }) {
            projectV2Item { id }
          }
        }
        """
        payload = self._request(
            "POST",
            self._graphql_url,
            {
                "query": mutation,
                "variables": {
                    "projectId": project_id,
                    "itemId": item_id,
                    "fieldId": field_id,
                    "optionId": option_id,
                },
            },
        )
        if not isinstance(payload, Mapping) or payload.get("errors"):
            raise GitHubError(f"GitHub GraphQL status mutation failed: {payload}")


def _print_plan(actions: Iterable[SyncAction], dry_run: bool) -> None:
    action_list = tuple(actions)
    prefix = "dry-run: " if dry_run else ""
    print(f"{prefix}{len(action_list)} project action(s) planned")
    for action in action_list:
        if action.kind == "add":
            print(f"{prefix}add issue #{action.issue_number} and set status to {action.status}")
        else:
            print(f"{prefix}set issue #{action.issue_number} status to {action.status}")


def reconcile(
    client: GitHubClient,
    repository: str,
    organization: str,
    project_number: int,
    dry_run: bool = False,
) -> int:
    issues = client.list_roadmap_issues(repository)
    default_branch = client.get_default_branch(repository)
    pull_requests = client.list_open_pull_requests(repository)
    project = client.get_project_snapshot(organization, project_number)
    actions = build_sync_plan(
        issues,
        pull_requests,
        project["items"],
        repository,
        default_branch,
    )
    _print_plan(actions, dry_run)
    if dry_run:
        return 0

    project_id = project["id"]
    field_id = project["status_field_id"]
    options = project["status_options"]
    for action in actions:
        item_id = action.item_id
        if action.kind == "add":
            if not isinstance(action.content_id, str):
                raise GitHubError(f"issue #{action.issue_number} add action has no content id")
            item_id = client.add_project_item(project_id, action.content_id)
        if not isinstance(item_id, str):
            raise GitHubError(f"issue #{action.issue_number} status action has no item id")
        option_id = options.get(action.status)
        if not isinstance(option_id, str):
            raise GitHubError(f"project Status field has no option named {action.status!r}")
        client.update_project_status(project_id, item_id, field_id, option_id)
    return 0


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", default=os.environ.get("BOARD_REPOSITORY", DEFAULT_REPOSITORY))
    parser.add_argument("--organization", default=os.environ.get("BOARD_ORGANIZATION", DEFAULT_ORGANIZATION))
    parser.add_argument("--project-number", type=int, default=int(os.environ.get("BOARD_PROJECT_NUMBER", DEFAULT_PROJECT_NUMBER)))
    parser.add_argument("--dry-run", action="store_true", help="read and print the plan without mutating the project")
    return parser.parse_args(argv)


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parse_args(argv)
    token = os.environ.get("PROJECT_TOKEN") or os.environ.get("GH_TOKEN")
    try:
        client = GitHubClient(
            token or "",
            api_url=os.environ.get("GITHUB_API_URL", "https://api.github.com"),
            graphql_url=os.environ.get("GITHUB_GRAPHQL_URL"),
        )
        return reconcile(client, args.repository, args.organization, args.project_number, args.dry_run)
    except (GitHubError, ValueError) as error:
        print(f"project sync failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
