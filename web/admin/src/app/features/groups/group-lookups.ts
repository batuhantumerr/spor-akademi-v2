import { Api } from '../../api/api';
import { apiV1CoachesGet, apiV1FacilitiesGet, apiV1SportsGet } from '../../api/functions';
import { NamedReferenceDto } from '../../api/models';

/** Active sports, facilities and coaches for the group filters and the group form. */
export interface GroupLookups {
  sports: NamedReferenceDto[];
  facilities: NamedReferenceDto[];
  coaches: NamedReferenceDto[];
}

const PAGE_SIZE = 100; // The API maximum.

/** Reads every page of a paged endpoint (a school has tens of these, not thousands). */
async function all<T>(
  fetch: (page: number) => Promise<{ items: T[]; totalPages: number | string }>,
): Promise<T[]> {
  const first = await fetch(1);
  const items = [...first.items];
  for (let page = 2; page <= Number(first.totalPages); page++) {
    items.push(...(await fetch(page)).items);
  }
  return items;
}

const byName = (a: NamedReferenceDto, b: NamedReferenceDto) => a.name.localeCompare(b.name, 'tr');

export async function loadGroupLookups(api: Api): Promise<GroupLookups> {
  const [sports, facilities, coaches] = await Promise.all([
    all((page) =>
      api.invoke(apiV1SportsGet, { includeArchived: false, page, pageSize: PAGE_SIZE }),
    ),
    all((page) =>
      api.invoke(apiV1FacilitiesGet, { includeArchived: false, page, pageSize: PAGE_SIZE }),
    ),
    all((page) => api.invoke(apiV1CoachesGet, { page, pageSize: PAGE_SIZE })),
  ]);
  return {
    sports: sports.map(({ id, name }) => ({ id, name })).sort(byName),
    facilities: facilities.map(({ id, name }) => ({ id, name })).sort(byName),
    coaches: coaches
      .filter((c) => c.status === 'Active')
      .map((c) => ({ id: c.id, name: `${c.firstName} ${c.lastName}` }))
      .sort(byName),
  };
}
