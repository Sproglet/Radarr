import React from 'react';
import SelectInput from 'Components/Form/SelectInput';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import MovieEdition from 'Movie/MovieEdition';
import { InputChanged } from 'typings/inputs';
import translate from 'Utilities/String/translate';

interface ParseEditionSelectorProps {
  movieId?: number;
  movieEditionId?: number;
  isFetching: boolean;
  onChange: (change: InputChanged<string>) => void;
}

function ParseEditionSelector({
  movieId,
  movieEditionId,
  isFetching,
  onChange,
}: ParseEditionSelectorProps) {
  const { data, error, isPending } = useApiQuery<MovieEdition[]>({
    path: `/movieedition?movieId=${movieId}`,
    queryOptions: {
      enabled: !!movieId,
      staleTime: 0,
      refetchOnMount: 'always',
    },
  });

  if (!movieId) {
    return null;
  }

  return (
    <div>
      <label>
        {translate('Edition')}
        <SelectInput
          name="movieEditionId"
          value={movieEditionId ?? 0}
          values={[
            { key: 0, value: translate('Automatic') },
            ...(data ?? []).map((target) => ({
              key: target.id,
              value: target.name,
            })),
          ]}
          isDisabled={isFetching || isPending || !!error}
          onChange={onChange}
        />
      </label>
      <p>{translate('EditionParserSelectionHelp')}</p>
      {error ? <p>{translate('EditionParserTargetsError')}</p> : null}
    </div>
  );
}

export default ParseEditionSelector;
