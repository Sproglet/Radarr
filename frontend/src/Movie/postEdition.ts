import { ApiError } from 'Helpers/Hooks/useApiQuery';

export default async function postEdition<T>(
  path: string,
  body: object
): Promise<T> {
  const response = await fetch(window.Radarr.apiRoot + path, {
    method: 'POST',
    headers: {
      'X-Api-Key': window.Radarr.apiKey,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    const error = await response.json().catch(() => undefined);
    throw new ApiError(path, response.status, response.statusText, error);
  }
  return response.json() as Promise<T>;
}
